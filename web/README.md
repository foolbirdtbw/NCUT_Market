# NCUT Market — 前端

北方工业大学校园二手交易平台的前端。无构建步骤：没有 npm、没有打包器、没有 TypeScript。
浏览器直接加载这里的文件，由 `NCUT_Market.Api` 同源托管。

## 文件

| 路径 | 说明 |
|---|---|
| `index.html` | 唯一页面。顶栏 + 一个 `<main id="view">`，视图整块替换 |
| `css/site.css` | 全部样式。亮/暗两套令牌在 `:root` 和 `[data-theme="dark"]` |
| `js/api.js` | 请求入口。定义 `window.api`，在这里把 problem+json 解析成 Error |
| `js/app.js` | 三个视图、哈希路由、事件委托、主题 |
| `vendor/jquery-3.7.1.min.js` | jQuery，原样入库 |

## 运行

```bash
dotnet run --project src/NCUT_Market.Api --launch-profile http
```

打开 http://localhost:5087/ 。静态目录由 `Configuration/StaticWebRoot.cs` 解析，启动日志里有：

```
Serving frontend static files from <仓库根>\web
```

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

**1. 服务端字符串插进 HTML 前必须过 `esc()`。** 视图整体用 `.html()` 重建，所以
`app.js` 里每个 `${}` 位置都要转义。改这片代码时留意别漏。

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
| 无商品功能 | 后端只有分类和宿舍区两个字典接口 |
| 无前端自动化测试 | `out/check-frontend.js` 能测 `treeHtml` / `formatDateTime` 两个纯函数，但没有提交进仓库 |
| `/favicon.ico` 404 | 仓库里零图片文件 |
| `/README.md` 可公开访问 | 内容无敏感信息 |
| 前后端同源部署 | 前端绑在 API 进程上，所以没有 CORS 配置 |
