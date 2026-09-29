# NCUT Market — 前端

北方工业大学校园二手交易平台的前端。**无构建步骤**：没有 npm、没有打包器、没有 TypeScript。
浏览器直接加载这里的文件，由 `NCUT_Market.Api` 同源托管。

## 目录

| 路径 | 说明 |
|---|---|
| `index.html` | 唯一的 HTML 页面。app shell、三个视图容器、SVG 图标精灵、toast 宿主 |
| `css/site.css` | 全部样式。令牌 → 重置 → 布局 → 组件 → 视图 → 响应式，单文件平铺 |
| `js/api.js` | 统一请求层。定义 `window.NcutApi`，是全站唯一发请求的地方 |
| `js/app.js` | 状态、格式化、哈希路由、三个视图、事件委托、主题切换 |
| `vendor/jquery-3.7.1.min.js` | jQuery，原样入库，未经任何修改 |

文件数刻意保持得很少。没有构建步骤时，拆分文件的唯一收益是可读性，两个 JS 文件已经够用。

## 第三方依赖

只有 jQuery 一个。

| 项 | 值 |
|---|---|
| 版本 | 3.7.1（官方压缩版） |
| 来源 | `https://code.jquery.com/jquery-3.7.1.min.js` |
| 字节数 | 87,533 |
| SHA-256 | `fc9a93dd241f6b045cbff0481cf4e1901becd0e12fb45166a8f17f95823f0b1a` |
| 许可证 | MIT — https://jquery.org/license/ |
| 引入方式 | 本地文件（`<script src="vendor/...">`），**不使用 CDN** |

校验：

```bash
sha256sum web/vendor/jquery-3.7.1.min.js
```

哈希是刻意记在这里的：vendoring 第三方代码而不记录来源和校验值，等于把「这个文件是从哪来的、
有没有被改过」变成无法回答的问题。

**这个文件绝不能编辑。** 改一个字节哈希就对不上。`.gitattributes` 里的 `web/vendor/** -text`
是为了同一件事：本机 `core.autocrlf=true`，没有这条规则 git 会在下一次 checkout 时把 LF 换成
CRLF，文件长度和哈希都会变——而那时它会表现得像一次供应链事故，而不是一个行尾问题。

**为什么是 3.7.1 而不是 4.0.0：** 4.0.0 已经 GA，但删掉了一批 3.x 里已弃用的 API，生态和文档
也还不如 3.x 全。

## 与 EasyERP 的关系

本项目的姊妹工程 EasyERP（`D:\CODE\workspace\AAA_work_experience\NET_ERP`）是主要的约定来源，
但前端有两处**结构性偏离**，是明确选择而不是疏忽：

1. **jQuery。** EasyERP 前端零第三方库：3 个手写文件，原生 ES module + `fetch` + 模板字符串 +
   自写的 `$` / `$$` 别名。所以「对齐它的库加载策略」这件事没有对象。
2. **托管位置。** EasyERP 放在 `src/EasyERP.Api/wwwroot/`，靠 Web SDK 约定自动生效。这里放在
   仓库根的 `web/`，在项目目录之外，所以需要手写 `PhysicalFileProvider` 接线和一条 MSBuild 拷贝项。

照搬过来的约定：统一 `api()` 请求层、分页文案与「边界处 disabled 而不是隐藏」、`:root` +
`[data-theme="dark"]` 重声明同一批令牌、哈希路由、内联错误卡 + 重试、`toast()` 承担所有反馈
（绝不用 `alert()`）、内联 SVG `<symbol>` 图标精灵、kebab-case 类名、`.skip-link` /
`:focus-visible` / `aria-live`、`?v=YYYYMMDD.N` 手动缓存串。

刻意没照搬：它的 921 行单文件 `app.js`（jQuery 让每个关注点都更啰嗦，一个文件会失控），
它的 `esc()` + 模板字符串 XSS 方案（换成 jQuery 后正确的规则是反的，见下），以及它「每次渲染后
重新绑定事件」的写法（用委托绑一次即可）。

## 约定

### XSS：规则是反的

> **服务端来的字符串只能通过 `.text()` / `.val()` / `.attr()` 进入 DOM。HTML 字符串只允许用于
> 不含任何服务端数据的本地构造标记。**

理由不是「`.text()` 更安全」——两者都能被写错——而是**默认路径的方向变了**。jQuery 里 `.text()`
是自然写法、`.html()` 需要刻意敲；模板字符串里反过来，插值默认就是 HTML，必须记得加 `esc()`。
让安全的那条路成为顺手的路，比依赖每次都记得加包装可靠。

表格单元格统一走 `cell()`：它只接受字符串（走 `.text()`）或已构造好的 jQuery/Node（走
`.append`），没有第三个分支。

**不要在动态内容里构造 `<svg>`。** jQuery 用 `document.createElement` 建元素，`<svg>` 会落在
HTML 命名空间里，渲染为空。`index.html` 里的图标精灵之所以没问题，是因为 HTML 解析器自己会给
它分配 SVG 命名空间。动态内容里的图标用 CSS 画（见 `.tree-node::before`）。

### 时间显示

接口发的是**北京时间的墙上时钟读数，没有时区后缀**：`"2026-09-29T13:43:09.123"`。

不要对它 `new Date()`。按 ES 规范，日期**时间**形式缺偏移量时按**浏览器本地时间**解析（而日期
**只有日期**的形式才按 UTC），所以 `new Date(v).toLocaleString()` 在任何非 UTC+8 的机器上都会
显示错的时间——**而且在开发机（东八区）上永远是对的**，这正是它危险的地方。

`formatDateTime()` 因此只做正则切片和字符串重排，全程不构造 `Date`。

真正的修法是后端加一个 `JsonConverter<DateTime>` 输出 `+08:00`，让契约本身没有歧义。本阶段是
纯前端，没有做。

### 事件绑定

委托绑定，只绑一次（绑在 `document` 上）。`data-action` / `data-page` / `data-retry` 这些属性是
「动作声明」，重渲染不会让它们失效。这样也就消掉了一整类 bug：重渲染后忘了重新绑定，表现为
「按钮第二次进入页面才没反应」。

### 分类树

`/api/categories` 返回**扁平分页列表**，树在客户端按 `parentId` 重建。两种行会因为处理不当而
静默丢失，`buildTree()` 都做了处理：

- **孤儿**：父分类被停用但子分类仍然启用时，子分类带着一个不在结果集里的 `parentId` 返回。
  它被**提升为根节点**，而不是被丢掉。
- **环**：`parentId` 互相指向（A→B→A）时，两行都不满足「有父节点」也不满足「有根」，会同时
  从两个分支消失。可达性扫描会兜住这种情况，同样提升为根。

树和分页是矛盾的：按页切出来的子集重建出的树是残缺的。所以分类页用 `pageSize=100`
（`MaxPageSize` 上限）一次拉足，`totalCount > 100` 时在页面顶部显示明确的告警，而不是静默渲染
一棵看起来完整的残缺树。

### 缓存串

`index.html` 里的 `?v=YYYYMMDD.N` 是手改的。`UseStaticFiles` 已经发 ETag 和 Last-Modified，
浏览器会带 `If-None-Match` 回验，所以它只是保险，不是必需品。

## 已知限制

| 项 | 说明 |
|---|---|
| 无商品功能 | 后端目前只有分类和宿舍区两个字典接口，商品接口未实现。首页对此有明确说明 |
| 无自动化测试 | 与「无构建步骤」直接冲突。`node --check web/js/*.js` 只能拦语法错误，拦不住逻辑错误 |
| `/favicon.ico` 返回 404 | 仓库里零图片文件，图标走内联 SVG 精灵。要消掉就得加一个图标文件 |
| `/README.md` 可被公开访问 | 它在静态目录里。内容只有版本、哈希、许可证和约定，没有敏感信息 |
| 分类树与分页结构性错配 | 用「pageSize=100 + 超限告警」把限制做成可见的。修法是后端加不分页的 `/api/categories/tree` |
| 时间无时区后缀 | 前端已按墙上时钟字符串正确渲染，但契约本身仍有歧义，别的消费者仍可能踩 |
| 前后端同源部署 | 前端绑在 API 进程上。这个方案是明确选择的，所以没有 CORS 配置 |

## 本地运行

```bash
dotnet run --project src/NCUT_Market.Api --launch-profile http
```

然后打开 http://localhost:5087/ 。

`web/` 目录的解析由 `src/NCUT_Market.Api/Configuration/StaticWebRoot.cs` 负责，按三个候选目录
依次尝试（配置覆盖 → `../../web` 开发布局 → `web/` 发布布局），**并把选中的目录打进启动日志**。
启动日志里找这一行：

```
Serving frontend static files from D:\CODE\workspace\C#\NCUT_Market\web
```

找不到任何候选目录时不会阻止 API 启动，只是静态文件不服务，并在日志里给出警告。
