/* 对前端里的纯函数做检查。都不碰 DOM，所以用 Node 直接跑，零依赖。
 * 函数体是从源文件里按大括号匹配抠出来的，不是在这里重打一遍，所以不会和实际代码脱节。
 *
 * esc / formatDateTime / formatPrice / conditionText / statusText / categoryOptions 在 ui.js，
 * treeHtml 在 app.js——它们原本都在 app.js 里，商品页面要共用才搬了出去。
 *
 * 注意：必须在 PowerShell 里跑。Git Bash 会把 TZ 变量吞掉，那样多次运行其实是同一个时区。
 */
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..", "web", "js");

const sources = {
  "ui.js": fs.readFileSync(path.join(root, "ui.js"), "utf8"),
  "app.js": fs.readFileSync(path.join(root, "app.js"), "utf8")
};

/** 扫到与起始括号配对的收尾字符，返回这一段源码。 */
function scan(source, start, open, close) {
  let depth = 0;

  for (let j = source.indexOf(open, start); j < source.length; j++) {
    if (source[j] === open) depth++;
    else if (source[j] === close && --depth === 0) return source.slice(start, j + 1);
  }

  throw new Error("括号不配对：" + source.slice(start, start + 40));
}

/**
 * 按名字找一段源码。ui.js 优先——共用的那些都在那边。
 *
 * 函数和模块级常量都要能抠出来：conditionText 读 CONDITIONS，statusText 读 STATUSES，
 * 少了那张表 eval 出来就是个 ReferenceError。
 */
function extract(name) {
  for (const file of ["ui.js", "app.js"]) {
    const source = sources[file];

    const fn = source.indexOf("function " + name + "(");

    if (fn !== -1) return scan(source, fn, "{", "}");

    // var 形式。从 = 开始数括号，停在最外层的分号。
    const variable = source.indexOf("var " + name + " =");

    if (variable !== -1) {
      let depth = 0;

      for (let j = source.indexOf("=", variable); j < source.length; j++) {
        const c = source[j];

        if (c === "[" || c === "{" || c === "(") depth++;
        else if (c === "]" || c === "}" || c === ")") depth--;
        else if (c === ";" && depth === 0) return source.slice(variable, j + 1);
      }

      throw new Error(file + " 里 " + name + " 的声明没有结尾分号");
    }
  }

  throw new Error("找不到 " + name);
}

/* 每个被 eval 的片段都要带上它自己依赖的函数。esc 是几乎所有人都要的，
 * categoryOptions 和 treeHtml 都直接调它。 */
function wrap(names, returnName, prelude) {
  const body = names.map(extract).join("");

  return eval("(function () { " + (prelude || "") + body + " return " + returnName + "; })()");
}

const esc = wrap(["esc"], "esc");
const formatDateTime = wrap(["formatDateTime"], "formatDateTime");
const formatPrice = wrap(["formatPrice"], "formatPrice");
const conditionText = wrap(["CONDITIONS", "conditionText"], "conditionText");
const statusText = wrap(["STATUSES", "statusText"], "statusText");
const categoryOptions = wrap(["esc", "categoryOptions"], "categoryOptions");
// treeHtml 在 app.js 里，调的是 NM.esc 而不是裸的 esc。补一个最小的 NM 顶上。
const treeHtml = wrap(["esc", "treeHtml"], "treeHtml", "var NM = { esc: esc };");

let failures = 0;

function check(label, actual, expected) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a === e) {
    console.log("  PASS  " + label);
  } else {
    failures++;
    console.log("  FAIL  " + label + "\n          期望 " + e + "\n          实际 " + a);
  }
}

/** 把渲染出来的 HTML 压成 ["名字@层级", ...]，只看结构。 */
function shape(html) {
  const tokens = html.match(/<ul[^>]*>|<\/ul>|<span class="tree-name">[^<]*<\/span>/g) || [];
  const out = [];
  let depth = 0;

  for (const token of tokens) {
    if (token === "</ul>") {
      depth--;
    } else if (token.startsWith("<ul")) {
      depth++;
    } else {
      out.push(token.replace(/<[^>]*>/g, "") + "@" + (depth - 1));
    }
  }

  return out;
}

console.log("\nesc()");

check("尖括号", esc("<b>"), "&lt;b&gt;");
check("双引号", esc('a"b'), "a&quot;b");
check("单引号", esc("a'b"), "a&#39;b");
check("和号", esc("a&b"), "a&amp;b");
check("null", esc(null), "");
check("数字", esc(12), "12");

console.log("\nformatDateTime()");

// 结构性断言：函数体里完全没有 Date。不构造 Date 就不受浏览器时区影响。
check("函数体里没有 Date", /\bDate\b/.test(extract("formatDateTime")), false);

check("墙上时钟字符串按原文重排", formatDateTime("2026-09-29T13:43:09.037"), "2026-09-29 13:43");
check("午夜不跳变", formatDateTime("2026-01-02T00:00:00"), "2026-01-02 00:00");
check("深夜不跳变", formatDateTime("2026-12-31T23:59:59.999"), "2026-12-31 23:59");
check("null", formatDateTime(null), "—");
check("undefined", formatDateTime(undefined), "—");
check("空字符串", formatDateTime(""), "—");

console.log("\nformatPrice()");

check("整数补两位", formatPrice(10), "¥10.00");
check("小数", formatPrice(12.5), "¥12.50");
check("零点五", formatPrice(0.5), "¥0.50");
check("零", formatPrice(0), "¥0.00");
check("字符串数字", formatPrice("3.1"), "¥3.10");

// 服务端给的是 decimal，正常到不了这里；到了也不能让页面出现 "¥NaN"。
check("不是数字时退化成 0", formatPrice("abc"), "¥0.00");

console.log("\nconditionText() / statusText()");

// 枚举在接口上是数字，索引即枚举值。越界给兜底文案，不显示 undefined。
check("成色 1", conditionText(1), "全新");
check("成色 4", conditionText(4), "明显使用痕迹");
check("成色 0（哨兵值）", conditionText(0), "未知成色");
check("成色 9（越界）", conditionText(9), "未知成色");
check("成色 undefined", conditionText(undefined), "未知成色");

check("状态 1", statusText(1), "草稿");
check("状态 2", statusText(2), "在售");
check("状态 3", statusText(3), "已售出");
check("状态 4", statusText(4), "已下架");
check("状态越界", statusText(7), "未知状态");

console.log("\ncategoryOptions()");

const options = [
  { id: 1, name: "root", parentId: null },
  { id: 2, name: "child", parentId: 1 },
  { id: 3, name: "grandchild", parentId: 2 }
];

check(
  "按层级缩进，父在前",
  categoryOptions(options, null)
    .replace(/<option value="\d+"[^>]*>/g, "")
    .replace(/<\/option>/g, "|")
    .split("|")
    .filter(Boolean),
  ["root", "　child", "　　grandchild"]
);

check(
  "选中项带 selected",
  categoryOptions(options, 2).includes('<option value="2" selected>'),
  true
);

check(
  "没选中时不带 selected",
  categoryOptions(options, null).includes("selected"),
  false
);

check(
  "分类名里的 HTML 被转义",
  categoryOptions([{ id: 1, name: '<script>x</script>', parentId: null }], null).includes("&lt;script&gt;"),
  true
);

/* 父节点不在结果集里的子节点渲染不出来——和 treeHtml 是同一个已知取舍，
 * 这里记一笔是为了说明下拉框和树的行为一致。 */
check(
  "父节点不在结果集里的子节点不出现在下拉里（已知取舍）",
  categoryOptions([{ id: 9, name: "orphan", parentId: 404 }], null),
  ""
);

console.log("\ntreeHtml()");

check("空列表", treeHtml([], null, 0), "");

check(
  "三个根节点平铺",
  shape(treeHtml([
    { id: 1, name: "a", parentId: null },
    { id: 2, name: "b", parentId: null }
  ], null, 0)),
  ["a@0", "b@0"]
);

check(
  "三层嵌套",
  shape(treeHtml([
    { id: 1, name: "root", parentId: null },
    { id: 2, name: "mid", parentId: 1 },
    { id: 3, name: "leaf", parentId: 2 }
  ], null, 0)),
  ["root@0", "mid@1", "leaf@2"]
);

check(
  "同级保持接口给的顺序",
  shape(treeHtml([
    { id: 10, name: "first", parentId: null },
    { id: 11, name: "second", parentId: null }
  ], null, 0)),
  ["first@0", "second@0"]
);

check(
  "父节点不在结果集里的子节点不渲染（已知取舍）",
  shape(treeHtml([{ id: 9, name: "orphan", parentId: 404 }], null, 0)),
  []
);

check(
  "分类名里的 HTML 被转义",
  treeHtml([{ id: 1, name: '<script>x</script>', parentId: null }], null, 0).includes("&lt;script&gt;"),
  true
);

console.log(
  failures === 0
    ? "\n全部通过（时区 " + Intl.DateTimeFormat().resolvedOptions().timeZone +
      "，偏移 " + new Date().getTimezoneOffset() + " 分钟）\n"
    : "\n" + failures + " 项失败\n"
);

process.exit(failures === 0 ? 0 : 1);
