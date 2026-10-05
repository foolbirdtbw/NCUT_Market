# NCUT Market — 前端

北方工业大学校园二手交易平台的前端。无构建步骤：没有 npm、没有打包器、没有 TypeScript。
浏览器直接加载这里的文件，由 `NCUT_Market.Api` 同源托管。

## 文件

| 路径 | 说明 |
|---|---|
| `index.html` | 唯一页面。顶栏 + 公告条 + 一个 `<main id="view">`，视图整块替换 |
| `css/site.css` | 全部样式。皮肤是「网页粗野主义」：纯白底、纯黑字、蓝下划线链接、直角、零过渡零阴影零渐变。深色模式是同一套东西的黑白反相，两套令牌分别在 `:root` 和 `[data-theme="dark"]` |
| `js/ui.js` | 渲染小工具，挂 `window.NM`。`esc()` / `formatDateTime()` 和几个枚举翻译都在这里，所有页面共用 |
| `js/api.js` | 请求入口。定义 `window.api`，在这里把 problem+json 解析成 Error |
| `js/auth.js` | token 的存取、当前用户、登录注册页、顶栏用户区 |
| `js/products.js` | 商品的列表、详情、发布、编辑、我的商品 |
| `js/messages.js` | 私信：会话列表、会话详情、发消息、顶栏未读徽标 |
| `js/announcements.js` | 公告：顶部条、公告页、管理员的发布与删除 |
| `js/app.js` | 哈希路由、事件委托、主题、启动时的几处拉取 |
| `vendor/jquery-3.7.1.min.js` | jQuery，原样入库 |

加载顺序有意义：`app.js` 最后，它启动时就要用到 `auth` / `messages` /
`announcements`。路由和处理函数都在各模块自己的文件里，`app.js` 只负责把它们接起来。

## 运行

```bash
dotnet run --project src/NCUT_Market.Api --launch-profile http
```

打开 http://localhost:5087/ 。静态目录由 `Configuration/StaticWebRoot.cs` 解析，启动日志里有：

```
Serving frontend static files from <仓库根>\web
```

## 开发数据

空库看不出任何东西——分页、`srcset`、未读徽标都要有数据才立得起来。

```powershell
powershell -File db/seed-demo.ps1            # 约 20 用户 / 60 商品 / 25 会话
powershell -File db/seed-demo.ps1 -Reset     # 先清掉上一次的，再重来
```

脚本走真实 HTTP 接口，不是直接写表：密码要真哈希，图片要真过一遍 ImageSharp（否则缩略图
文件不存在，列表页全是一片灰底占位），上架还有状态机。占位图用 `System.Drawing` 现场画，
不需要任何新依赖。

跑完会打印账号，也会写一份 `db/demo-accounts.txt`（已 gitignore）。密码统一是 `Demo12345!`，
管理员那个账号的 `role` 是脚本用 SQL 提上去的——仓库里没有提权接口，这是唯一的手段。

**不加 `-Reset` 重跑是累加的**，用户名带随机后缀所以不会冲突，但数据会翻倍。

## jQuery

| 项 | 值 |
|---|---|
| 版本 | 3.7.1 |
| 来源 | `https://code.jquery.com/jquery-3.7.1.min.js` |
| 字节数 | 87,533 |
| SHA-256 | `fc9a93dd241f6b045cbff0481cf4e1901becd0e12fb45166a8f17f95823f0b1a` |
| 许可证 | MIT |

```bash
sha256sum web/vendor/jquery-3.7.1.min.js
```

**这个文件不要编辑**，改一个字节哈希就对不上。`.gitattributes` 里的 `web/vendor/** -text`
是必须的：本机 `core.autocrlf=true`，否则 git 会在下次 checkout 时把 LF 换成 CRLF，
文件长度和哈希都变。

## 两条不能省的写法

这两条不是"防患于未然"，是"这样写才对"：

**1. 服务端字符串插进 HTML 前必须过 `esc()`。** 视图整体用 `.html()` 重建，
所以每个拼进 HTML 的位置都要转义——标题、描述、昵称、消息正文、公告正文、错误详情。
这些视图分散在 `products.js` / `messages.js` / `announcements.js` 里，改哪一片都要留意。

配套的一条：正文里的换行交给 CSS 的 `white-space: pre-wrap`，**不要把 `\n` 替成 `<br>`**
再拼进去——替了就等于把用户输入又拼了一次 HTML。`ui.js` 里那句可以直接照抄。

**2. 时间字符串不要 `new Date()`。** 接口发的是北京时间的墙上时钟读数，没有时区后缀
（`"2026-09-29T13:43:09.037"`）。按 ES 规范，日期**时间**形式没偏移量时按**浏览器本地时间**
解析，所以 `new Date(v).toLocaleString()` 在任何非 UTC+8 的机器上都显示错——**唯独在开发机
（东八区）上是对的**，所以测不出来。直接按文本重排：

```js
value.replace("T", " ").slice(0, 16)
```

## 已知取舍

| 项 | 说明 |
|---|---|
| 分类树不处理孤儿 | 父分类被停用时，子分类的 `parentId` 不在结果集里，那一支**不会渲染**。现在的数据不会出现这种情况，真出现了再处理 |
| 分类一次拉 100 条 | 接口分页、树不分页，所以按 `MaxPageSize` 一次拉足，超过 100 个分类就只能显示前 100 个 |
| 会话只显示最新一页消息 | 接口给的是最新 20 条，页面会写明「共 N 条」。往上翻旧消息这一轮不做 |
| 未读数不轮询 | 顶栏那个数字只在页面加载、登录状态变化、读完一个会话之后刷新。两个人同时开着页面，对方发来的消息不会自动跳出来 |
| 公告条按 id 记「已关闭」 | 关掉之后存在 localStorage 里，刷新不再弹；但发一条新的照样会出现 |
| 前端测试只覆盖纯函数 | `out/check-frontend.js` 测 `esc` / `formatDateTime` / `treeHtml` 这些不碰 DOM 的函数，外加一道「use strict 下有没有漏声明变量」。视图和请求得靠手点 |
| 访问计数器是装饰 | 页脚那个数字读 localStorage 自增，不接任何统计，刷新几遍就「访问」了好几次 |
| 正文 14px 宋体，不是 12px | 当年确实是 12px，但 SimSun 在高分屏上低于 13px 就糊了——那是坏了不是复古。想还原的话改 `:root` 的 `body { font-size }` |
| 按钮面是 `#dfdfdf`，不是纯白 | 「背景只能是纯白」和「按钮必须有 Windows 95 斜面」是打架的——白面配白色上/左边框，斜面根本看不见。所以按钮用系统银灰面，其余一切纯白。输入框同理，凹进去 |
| 弱化色没做成纯黑 | 规范要求正文弱化色也用纯黑、靠结构分层次。但 12px 的成色/时间/页码全黑会糊成一片，用了 `#444444` |
| 只有正文链接有 hover 变色 | 按钮没有 hover 态——当年的按钮就没有，状态只发生在按下那一瞬间。卡片和列表行的 hover 只改边框色或字色，不改底色 |
| 复古感靠结构不靠动效 | 没有 `<marquee>`，没有闪烁，也没有扫描线。零过渡、零动画 |
| 深色模式是黑白反相，不是 CRT | 早先那版黑底绿字终端已经去掉。现在两套主题是同一批令牌换值，组件规则一行不重写；链接在纯黑上只有 1.9:1，所以反相时提亮成 `#66ccff` |
| `/favicon.ico` 404 | 仓库里零图片文件 |
| `/README.md` 可公开访问 | 内容无敏感信息 |
| 前后端同源部署 | 前端绑在 API 进程上，所以没有 CORS 配置 |

`out/` 整体是构建产物，只有 `check-frontend.js` 被 `.gitignore` 反向包含进来。**用 PowerShell 跑**：

```powershell
node out/check-frontend.js
```

Git Bash 会把 `TZ` 吞掉，那样连跑几次其实都是同一个时区，测了等于没测。
