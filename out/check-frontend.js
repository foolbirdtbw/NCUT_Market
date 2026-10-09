/* 对前端里的纯函数做检查。都不碰 DOM，所以用 Node 直接跑，零依赖。
 * 函数体是从源文件里按大括号匹配抠出来的，不是在这里重打一遍，所以不会和实际代码脱节。
 *
 * esc / formatDateTime / formatPrice / conditionText / statusText / userStatusText /
 * categoryOptions 在 ui.js，treeHtml 在 dictionaries.js——它们原本都在 app.js 里，
 * 商品页面要共用才搬了出去，分类树后来又跟着分类管理页搬到了 dictionaries.js。
 * coverLines / coverPaletteIndex 在 products.js，只有它们自己用。
 * visitDigits 在 app.js，是页脚那个六位方框，也归它自己。
 *
 * 注意：必须在 PowerShell 里跑。Git Bash 会把 TZ 变量吞掉，那样多次运行其实是同一个时区。
 */
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..", "web", "js");

// 每个文件都要读：下面那道"未声明就赋值"的检查是逐文件做的。
const files = [
  "ui.js", "auth.js", "api.js", "products.js",
  "messages.js", "notifications.js", "announcements.js", "feedback.js",
  "dictionaries.js", "users.js", "app.js"
];

const sources = {};

for (const file of files) {
  sources[file] = fs.readFileSync(path.join(root, file), "utf8");
}

/* 把注释和字符串字面量抹掉，只留代码骨架。
 *
 * 不这么做的话，注释里出现的等号会被当成赋值——这个文件顶上就写着
 * "out/ 是构建产物" 之类带英文等号的说明，误报会多到没法看。 */
function stripCommentsAndStrings(source) {
  let out = "";
  let i = 0;

  while (i < source.length) {
    const pair = source.substr(i, 2);

    if (pair === "//") {
      while (i < source.length && source[i] !== "\n") i++;
      continue;
    }

    if (pair === "/*") {
      i += 2;
      while (i < source.length && source.substr(i, 2) !== "*/") i++;
      i += 2;
      continue;
    }

    const c = source[i];

    if (c === '"' || c === "'" || c === "`") {
      i++;
      while (i < source.length && source[i] !== c) {
        i += source[i] === "\\" ? 2 : 1;
      }
      i++;
      out += '""';
      continue;
    }

    out += c;
    i++;
  }

  return out;
}

/* 找出"赋了值但从没声明"的标识符。
 *
 * 每个文件都是 "use strict" 的 IIFE，所以这不是风格问题：读或写一个没声明的名字
 * 会抛 ReferenceError。auth.js 里 pendingHash 就这样漏过一次——它在登录成功的回调
 * 和 401 的处理里各出现一次，抛出去的表现是"点了按钮没反应"，而且没有任何测试
 * 覆盖到，因为这里只测纯函数。
 *
 * 规则收得很紧，宁可漏报也不要误报：只认前面不是 . 也不是 $ 的裸标识符，
 * 声明来源包括 var/let/const/function 以及函数参数和 catch 绑定。
 * 右边是 => 的（箭头函数的参数）和 == / != 之类都排除掉。 */
function findUndeclaredAssignments(source) {
  const code = stripCommentsAndStrings(source);

  const declared = new Set();

  for (const m of code.matchAll(/\b(?:var|let|const|function)\s+([A-Za-z_$][\w$]*)/g)) {
    declared.add(m[1]);
  }

  // 函数参数。写成 (a, b) 或 (a, b = 1) 都算声明。
  for (const m of code.matchAll(/\bfunction\s*[A-Za-z_$\w]*\s*\(([^)]*)\)/g)) {
    for (const part of m[1].split(",")) {
      const name = part.split("=")[0].trim();

      if (/^[A-Za-z_$][\w$]*$/.test(name)) declared.add(name);
    }
  }

  // catch (e) 和 for (const x of ...) 里的 const 已经被上面覆盖，catch 没有。
  for (const m of code.matchAll(/\bcatch\s*\(\s*([A-Za-z_$][\w$]*)/g)) {
    declared.add(m[1]);
  }

  const offenders = [];

  for (const m of code.matchAll(/(?<![.\w$])([A-Za-z_$][\w$]*)\s*=(?!=)/g)) {
    const name = m[1];

    if (!declared.has(name) && !offenders.includes(name)) {
      offenders.push(name);
    }
  }

  return offenders;
}

/* 这段的执行放在文件末尾：check 和 failures 都在下面，在它们之前调用会撞上 TDZ。 */

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
  for (const file of ["ui.js", "dictionaries.js", "app.js", "products.js"]) {
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
const userStatusText = wrap(["USER_STATUSES", "userStatusText"], "userStatusText");
const feedbackKindText = wrap(["FEEDBACK_KINDS", "feedbackKindText"], "feedbackKindText");
const feedbackStatusText = wrap(["FEEDBACK_STATUSES", "feedbackStatusText"], "feedbackStatusText");
const categoryOptions = wrap(["esc", "categoryOptions"], "categoryOptions");
// treeHtml 在 dictionaries.js 里，调的是 NM.esc 而不是裸的 esc。补一个最小的 NM 顶上。
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

console.log("\nuserStatusText()");

/* 账号状态是另一张表。这里最容易出的错是"复用 statusText"——两张表都从 1 开始，
 * 编译器和类型都拦不住，而 UserStatus 的 2 正好撞上商品状态的 2。
 * 下面第一条就是钉死这件事的。 */
check("账号 1", userStatusText(1), "正常");
check("账号 2 和商品状态 2 不是一回事", userStatusText(2) === statusText(2), false);
check("账号 2", userStatusText(2), "已停用");
check("账号 0（哨兵值）", userStatusText(0), "未知状态");
check("账号越界", userStatusText(9), "未知状态");
check("账号 undefined", userStatusText(undefined), "未知状态");

console.log("\nfeedbackKindText() / feedbackStatusText()");

/* 第三、第四张枚举表。同样是"值撞车、复用就出错"的老问题：反馈状态的 2 是「已采纳」，
 * 商品状态的 2 是「在售」。 */
check("分类 1", feedbackKindText(1), "问题反馈");
check("分类 2", feedbackKindText(2), "功能建议");
check("分类越界", feedbackKindText(9), "未知类型");

check("反馈状态 1", feedbackStatusText(1), "待处理");
check("反馈状态 2 和商品状态 2 不是一回事", feedbackStatusText(2) === statusText(2), false);
check("反馈状态 4", feedbackStatusText(4), "不考虑");
check("反馈状态 0（哨兵值）", feedbackStatusText(0), "未知状态");
check("反馈状态越界", feedbackStatusText(9), "未知状态");

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

console.log("\ncoverLines() / coverPaletteIndex()");

/* 生成封面时唯二会静默出错的地方：切行切错只是难看，取色越界则不声不响地
 * 画成上一张的颜色（ctx.fillStyle 赋 undefined 不抛错），所以这两条值得钉住。 */
const coverLines = wrap(["COVER_LINE_CHARS", "COVER_MAX_LINES", "coverLines"], "coverLines");
const coverPaletteIndex = wrap(["COVER_PALETTE", "coverPaletteIndex"], "coverPaletteIndex");
const coverPalette = wrap(["COVER_PALETTE"], "COVER_PALETTE");

const eight = "一二三四五六七八";

check("空标题也有一行，不是空数组", coverLines(""), [""]);
check("短标题一行", coverLines("二手自行车"), ["二手自行车"]);
check("正好八个字不切", coverLines(eight), [eight]);
check("九个字切成两行", coverLines(eight + "九"), [eight, "九"]);
check(
  "二十四个字正好三行，一个字没丢所以不加省略号",
  coverLines(eight.repeat(3)),
  [eight, eight, eight]
);
check(
  "二十五个字仍然三行，末行让出一个字给省略号",
  coverLines(eight.repeat(3) + "十"),
  [eight, eight, "一二三四五六七…"]
);

check(
  "再长也不超过三行",
  coverLines(eight.repeat(20)).length <= 3,
  true
);

check(
  "每行都不超过八个字",
  coverLines(eight.repeat(20) + "尾").every(line => line.length <= 8),
  true
);

check(
  "同一个标题两次取到同一个颜色",
  coverPaletteIndex("二手自行车") === coverPaletteIndex("二手自行车"),
  true
);

/* 越界时 COVER_PALETTE[i] 是 undefined，而 ctx.fillStyle 赋 undefined 既不抛错也不生效，
 * 只是留着上一次的颜色——图会静默画错。空标题、超长标题、纯符号标题都要落在范围内。 */
const titles = [
  "",
  "x",
  "二手自行车",
  "！@#￥%……&*（）",
  eight.repeat(20) + "尾",
  "急出！九成新台灯，宿舍自提，价格可议"
];

check(
  "每个标题取到的下标都落在调色板范围内",
  titles.every(t => {
    const index = coverPaletteIndex(t);
    return Number.isInteger(index) && index >= 0 && index < coverPalette.length;
  }),
  true
);

check("调色板就是 seed 脚本那十个颜色", coverPalette.length, 10);

console.log("\nvisitDigits()");

/* 页脚那个六位方框。函数本身只是补零加切格子，但它唯一的输入来自一次网络请求，
 * 所以最后三条才是重点：拉不到、字段改名、后端回了 {} 的时候，这里必须退化成 000000，
 * 而不是把 undefined 补成 "00undefined" 摆到页脚上。 */
const visitDigits = wrap(["visitDigits"], "visitDigits");

const digitsOf = html => (html.match(/>(\d+)</g) || []).map(x => x.slice(1, -1)).join("");

check("零是六个零", visitDigits(0), "<span>0</span><span>0</span><span>0</span><span>0</span><span>0</span><span>0</span>");
check("补到六位", digitsOf(visitDigits(3)), "000003");
check("四位数字", digitsOf(visitDigits(1234)), "001234");
check("正好六位原样", digitsOf(visitDigits(999999)), "999999");
check("超过六位不截断", digitsOf(visitDigits(1234567)), "1234567");
check("小数向下取整", digitsOf(visitDigits(12.9)), "000012");
check("undefined 退化成零", digitsOf(visitDigits(undefined)), "000000");
check("null 退化成零", digitsOf(visitDigits(null)), "000000");
check("NaN 退化成零", digitsOf(visitDigits(NaN)), "000000");
check("负数退化成零", digitsOf(visitDigits(-5)), "000000");
check("字符串数字照样认", digitsOf(visitDigits("42")), "000042");
check("不是数字的字符串退化成零", digitsOf(visitDigits("abc")), "000000");

console.log("\n未声明就赋值的标识符（use strict 下会抛 ReferenceError）");

for (const file of files) {
  check(file, findUndeclaredAssignments(sources[file]), []);
}

console.log(
  failures === 0
    ? "\n全部通过（时区 " + Intl.DateTimeFormat().resolvedOptions().timeZone +
      "，偏移 " + new Date().getTimezoneOffset() + " 分钟）\n"
    : "\n" + failures + " 项失败\n"
);

process.exit(failures === 0 ? 0 : 1);
