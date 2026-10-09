# NCUT Market

北方工业大学校园二手交易平台。学生在上面发布闲置、互相私信谈价、走完一笔线上交易，管理员维护
分类、宿舍区、公告和用户。

后端是 ASP.NET Core，前端是**没有构建步骤**的 jQuery 页面（没有 npm、没有打包器、没有
TypeScript），由同一个进程同源托管。仓库里一个构建产物都没有：`dotnet run` 起来就是完整站点。

```
浏览器 ──┬── 静态文件（web/，PhysicalFileProvider 直读磁盘）
         └── /api/**（JWT bearer）── 服务层 ── EF Core ── MySQL 8.4
                                        └── data/uploads/（用户上传的商品图）
```

## 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 运行时 | .NET 10（`net10.0`，`TreatWarningsAsErrors`） | 定义在 `Directory.Build.props`，四个项目共用 |
| Web | ASP.NET Core 10.0.11 + `[ApiController]` | 控制器薄，逻辑都在服务层 |
| ORM | EF Core **9.0.19** | **不要再往上抬**：Pomelo 只到 9，EF Core 10 会和它打架 |
| 数据库 | MySQL 8.4 / Pomelo 9.0.0 | `utf8mb4` + `utf8mb4_0900_ai_ci` |
| 认证 | JWT bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`) | 无状态，服务端不存会话 |
| 密码 | `PasswordHasher<T>`（`Microsoft.Extensions.Identity.Core`） | PBKDF2，不是自研哈希 |
| 图片 | SixLabors.ImageSharp **3.1.12** | **不要升 4.x**：4.x 在 **构建期**强制校验 Six Labors 许可证，没有 key 直接编不过 |
| 文档 | Microsoft.AspNetCore.OpenApi + Scalar | 只在 Development 挂载 |
| 测试 | xUnit 2.9.3 + `WebApplicationFactory` | 打真实 MySQL，不是 InMemory |
| 前端 | jQuery 3.7.1（原样入库）+ 手写 CSS | 无构建，无 CDN |

## 目录

| 路径 | 说明 |
|---|---|
| `src/NCUT_Market.Core/` | 实体、枚举、DTO、服务接口、分页/错误码等公共类型。不依赖任何基础设施 |
| `src/NCUT_Market.Infrastructure/` | `AppDbContext`、EF 映射、迁移、服务实现、JWT、图片存储、后台任务 |
| `src/NCUT_Market.Api/` | 控制器、中间件、DI 装配、`Program.cs` |
| `tests/NCUT_Market.ApiTests/` | HTTP 层测试（`WebApplicationFactory`，真实数据库，串行跑） |
| `tests/NCUT_Market.IntegrationTests/` | 直接驱动 `AppDbContext` 的测试（模型、审计时间戳、分页） |
| `web/` | 整个前端。**静态文件只认这个目录**，见下 |
| `db/` | 开发种子：字典表 SQL、演示数据脚本 |
| `out/` | 构建产物目录（已 gitignore），只有 `out/check-frontend.js` 是源码 |

`web/` 的位置是有代价的，写在 `NCUT_Market.Api.csproj` 里：静态文件靠一条 `None Include="..\..\web\**\*"` 的 glob 进发布产物，所以**新加的静态文件必须放在 `web/` 下面**——放别处（比如 `db/`）开发时读得到，`dotnet publish` 之后就 404。

## 跑起来

需要 .NET 10 SDK 和 MySQL 8.4。

```powershell
# 1. 建库（库名要和下面连接串里的对上）
mysql -u root -p -e "CREATE DATABASE ncut_market CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"

# 2. 本地配置。.env 已 gitignore，仓库里只有占位值的 .env.example
Copy-Item .env.example .env
#    然后编辑 .env：填连接串密码，Jwt__SigningKey 换成随机值（至少 32 字节）

# 3. 建表。迁移是手工执行的，启动时不会自动建表
dotnet ef database update --project src/NCUT_Market.Infrastructure --startup-project src/NCUT_Market.Api

# 4. 起服务
dotnet run --project src/NCUT_Market.Api --launch-profile http
```

打开 <http://localhost:5087/>，接口文档在 <http://localhost:5087/scalar>。

空库看不出任何东西——分页、缩略图、未读徽标都要有数据才立得起来。两步，**顺序不能反**：

```powershell
# 1. 三个顶层分类和 1~3 号楼。seed-demo.ps1 要按名字去找它们的 id，没有会直接报错。
#    任何 MySQL 客户端都行，下面是命令行客户端的写法。
mysql -u root -p ncut_market < db/dev-seed.sql

# 2. 约 20 用户 / 60 商品 / 25 会话，并补上子分类和 4~6 号楼
powershell -File db/seed-demo.ps1
powershell -File db/seed-demo.ps1 -Reset      # 先清掉上次的再重来
```

`seed-demo.ps1` 走真实 HTTP 接口而不是直接写表：密码要真哈希、图片要真过一遍 ImageSharp、上架要过状态机。它只有三处直接写库（字典表补数据、提一个管理员、`-Reset` 清理），因为那些接口覆盖不到。连接串从 `.env` 读，不新增配置文件。不加 `-Reset` 重跑是**累加**的，用户名带随机后缀所以不冲突，但数据会翻倍。跑完会打印账号，密码统一是 `Demo12345!`。

## 配置

配置源只有两处，优先级反直觉，`Program.cs` 顶部写明白了：

1. **真环境变量**——只对 `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` 生效
2. **`.env`**（仓库根，`DotEnvLoader` 读进来作为**后加**的内存配置源）

配置源是后加的赢，所以 **`.env` 压过真环境变量**，`$env:ConnectionStrings__DefaultConnection` 是**不管用**的。这是有意的：本地设置只住在 `.env` 一个地方。副作用是测试工厂只能整个替换 `DbContext` 注册，不能靠改连接串。

`appsettings.json` 里那条 `Server=localhost;...;User=root;Password=;` 是**占位值**，照它连会 `Access denied`——真正生效的是 `.env`。

| 键 | 必需 | 说明 |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | 是 | `__` 会变成配置分隔符，即 `ConnectionStrings:DefaultConnection` |
| `Jwt__SigningKey` | **是** | 少于 32 字节直接拒绝启动。空 key 签出来的 token 谁都能伪造，而这种失败从外面看不出来 |
| `Environment` | 否 | 绑到 `ASPNETCORE_ENVIRONMENT`。不设的话 `dotnet run` 不带 profile 会跑成 Production，OpenAPI 和 Scalar 都不挂载 |
| `Storage__UploadRoot` | 否 | 上传图落盘位置，默认 `<content-root>/data/uploads` |
| `BackgroundJobs__Enabled` | 否 | 关掉交易扫尾后台任务。测试夹具就是这么关的 |

## 时间

**所有审计时间戳存的是北京时间，而这个 MySQL 实例被钉在 UTC**（`default-time-zone=+00:00`）。两边对不上，所以：

- 写入走 `AppDbContext.AuditNow` = `UtcNow.AddHours(8)`，固定 +8 而不是 `DateTime.Now`——中国 1991 年后不分夏令时、只有一个时区，+8 恒成立；`DateTime.Now` 会把「这几列是北京时间」这件事绑到跑代码那台机器的时区上，换个 UTC 主机整个表就整体偏移。
- 读回来是 `DateTimeKind.Unspecified`（连接串刻意不带 `DateTimeKind`），原样往返。
- 手写 SQL 必须自己补，像 `db/dev-seed.sql` 那样写 `UTC_TIMESTAMP(3) + INTERVAL 8 HOUR`；写 `NOW(3)` 会存进一个早 8 小时的值。
- `created_at` / `updated_at` 由 `SaveChanges` 统一盖。**`ExecuteUpdateAsync` / `ExecuteDeleteAsync` 绕过变更跟踪，不会被盖**——哪个文件里有它，哪个文件就得自己引用 `AuditNow` 手写时间戳。
- `Product.LastActivityAt` 不在自动盖章之列：它的语义是「这个商品被真的改过」，是留给**草稿清理任务**的时钟（超过 7 天没动过的草稿该删）。每次保存都盖就等于让废弃草稿永远删不掉。**那个任务还没写**——`Jobs/` 下目前只有交易扫尾一个。

## 业务

### 角色与账号

| | 说明 |
|---|---|
| `UserRole.User = 1` | 发布商品、私信、交易 |
| `UserRole.Admin = 2` | 额外的：公告发布/删除、分类增删改、宿舍区增删改、搜用户、发密码重置码 |
| `UserStatus.Active = 1` / `Disabled = 2` | **用户从不物理删除**，停用是唯一的移除方式（所有指向 `users` 的外键都是 `RESTRICT`） |

角色**不从 JWT 里读**，每次管理操作都回数据库查 `role == Admin && status == Active`（`IsAdminAsync`）。所以手工提权下一条请求就生效，不用重新登录。停用账号被挡在登录、重置密码、`GET /api/auth/me` 和管理操作之外——注意这里有个已知的缝：**停用之前签发的 token 在普通会员接口上照旧管用**，那些路径不复查状态。

**私信没有管理员后门**，这是有意的：读别人的私信不是管理员该有的权力。非参与方拿到的是 404 而不是 403，这样连「这个会话存不存在」都不会泄露。

### 认证

**注册**（`POST /api/auth/register`）先查一遍用户名/学号是否重复，给出干净的 409 和具体到哪个字段的提示；真正的保证还是那两个唯一索引。密码走 ASP.NET Core Identity 的 `PasswordHasher<User>`（PBKDF2），注册完直接返回 token，不用再登一次。

**登录**（`POST /api/auth/login`）用户名不存在和密码不对返回**同一个** `INVALID_CREDENTIALS`——不给出用户是否存在的信号。停用是在**验完密码之后**才查的，同样是为了不泄露账号存不存在。`SuccessRehashNeeded` 时会顺手重哈希并落库（只有这一刻明文在手上）。

**JWT** 是 HS256，密钥来自 `Jwt__SigningKey`，**没有默认值**——缺了直接拒绝启动。Claims 只有 `sub`（用户 id）、`unique_name`（用户名）、`jti`（随机 GUID），**没有角色 claim**（见上，角色每次回库查）。有效期 **7 天**，没有 refresh 流程。

**忘记密码走得是「管理员发码」，不是自助重置。**管理员在用户管理页对一个账号发一个 **8 位**一次性码（字母表去掉了 `I`/`O`/`0`/`1`，显示成 `XXXX-XXXX`），库里存的是**无盐 SHA-256 摘要**，明文只在发码的响应里出现一次。有效期 **24 小时**，重新发码会作废上一个。用户在匿名接口 `POST /api/auth/reset-password` 用它换新密码，比码用 `FixedTimeEquals`；用户不存在、没发过码、码不对、码过期**四种情况返回同一句话**，同样是不给探测信号。成功之后码被清掉（用一次即焚）。

### 商品状态机

`ProductStatus`：`Draft=1`、`Published=2`、`Sold=3`、`Offline=4`、`InTransaction=5`。**没有 `Deleted`**——删除是 `deleted_at` 标记，不是一种状态。

```
            创建
             │
             ▼
          ┌──────┐  上架(需≥1张图)  ┌───────────┐
          │Draft ├────────────────►│ Published │◄──┐
          └──┬───┘                 └─┬──┬───┬──┘   │
             │ 上架                   │  │   │      │ 下架(有人正在谈则拒绝)
             │                        │  │   └──────┘
             │        下架            │  │
             │◄───────────────────────┘  │ 手标已售出
             ▼                           ▼
          ┌────────┐                ┌──────┐
          │Offline │                │ Sold │
          └────────┘                └──────┘
                                          ▲
             接受交易提议                  │ 双方确认 / 超时结算
             ┌──────────────┐             │
             │InTransaction ├─────────────┘
             └──────┬───────┘
                    │ 超时且无人确认
                    └──────────► 退回 Published
```

几条硬规则：

- **上架前至少一张照片**，否则 400。
- **下架和手标售出都会被「有人正在谈」挡住**（任一关联会话 `transaction_proposed_at` 非空）→ 409。
- **删除先下架**：直接从 `Published` 删是 409「请先下架，再删除。」，不是一键从详情页删掉。
- 非在售的商品对**非卖家**是 404 而不是 403——不给「存在但你没权限」这种确认。

**删除分两种，看这笔是不是走平台成交的：**

| 情况 | 做法 |
|---|---|
| `transaction_buyer_id` **非空**（平台成交过） | **软删**：只写 `deleted_at`，行和图片文件都留着 |
| `transaction_buyer_id` 为空（手标售出 / 从没卖过） | **硬删**：行连图片一起真删 |

软删的原因是交易记录**不是副本**——它就是这一行上的 `transaction_*` 几列。行删了，私信里那块交易面板会整个塌成 null，双方确认收货/收款的整个过程跟着消失。所以那种「删除」商品从列表、搜索、「我的商品」里消失，详情页对所有人（卖家也算）404，但私信里那块面板原样留着。「我买到的」也刻意**不**过滤 `deleted_at`：那是买家的凭据，卖家整理自己的页面不该抹掉别人花过的钱。

### 交易流程

交易**不是一张表**，是 `products` 上的几列加 `conversations` 上的几列。

1. **开会话** — `POST /api/conversations`，带商品 id。按唯一索引 `(product_id, buyer_id)` find-or-create，重复点「联系卖家」拿到的是同一个会话（两次都是 200）。会话创建时把商品标题和缩略图**冻**在里面——之后商品被删了，会话里那串标题还在。不能联系自己。

   打开会话页即**已读**（`POST /api/conversations/{id}/read` 只盖自己那一侧的时间戳），发信人下次进来能在自己发出去的气泡上看到「已读」——比的是消息的 `created_at` 和对方的那个戳，两个都由服务端盖。没有轮询，所以不是实时的。

   会话可以**单方面从自己的列表里删掉**（`DELETE /api/conversations/{id}`）：删的是我这一侧，对方那边原封不动；任何一方再写一句话，两边一起恢复。唯一删不掉的是**交易进行中**的会话（回 409），因为确认收货/确认收款的按钮只存在于会话页里。

2. **发起交易** — `POST /api/conversations/{id}/transaction`。要求商品是 `Published`，且这个会话没有待接受的提议。写 `conversations.transaction_proposed_by_id` / `transaction_proposed_at`。**此时商品还在售**。
3. **接受** — `POST .../transaction/accept`，由**对方**点（发起人自己接受是 409）。写 `products.transaction_buyer_id = 会话的 buyer_id`、`transaction_accepted_at`，状态转 `InTransaction`，并**清掉同一个商品上其它会话的提议**（后来的把先前的挤掉）。
4. **拒绝 / 撤回** — `POST .../transaction/cancel`，**两边谁点都行**：不是发起方点叫拒绝，就是发起方点叫撤回。两者是同一个转换——清掉这两列、给对方写一条通知，标题按点击者是谁分两句（「交易提议被拒绝」/「交易提议已撤回」）。**只清这一条会话**，同一商品上别的会话的提议不受影响（和「接受」不同）。商品自始至终没动过，所以拒绝之后可以立刻重新发起，没有冷却期。既没有 403 也没有过期检查：`accept` 会占住商品，`cancel` 不会。
5. **双方确认** — 买家 `POST .../transaction/receipt`（确认收货），卖家 `POST .../transaction/payment`（确认收款）。各自只写自己那一列 `buyer_confirmed_at` / `seller_confirmed_at`。**两列都非空才算成交**：状态转 `Sold`、写 `sold_at`。这一步**幂等**——已经 `Sold` 了再点返回成功，不报错。

**超时兜底**（`TransactionSweepJob`，每 **15 分钟**一轮，`BackgroundJobs:Enabled` 可关）。`ProposalLifetime` 和 `TransactionLifetime` 都是 **1 天**，三趟：

| 趟 | 条件 | 结果 |
|---|---|---|
| 1 | 提议挂了超过 1 天没人应 | 清掉提议，通知发起人「交易提议已过期」 |
| 2 | `InTransaction` 超过 1 天，**至少一方**已确认 | 沉默那方视为同意，结算成 `Sold` |
| 3 | `InTransaction` 超过 1 天，**双方都没确认** | 退回 `Published`，清空所有 `transaction_*` 列 |

所以过了期限只有两种下场：**有人确认过 → 成交；一个人都没确认 → 自动回滚到在售。**

### 成交的两种，只有一种能进「我买到的」

判据是 `transaction_buyer_id`，不是 `status`：

| | 卖家手标已售出 | 走完平台交易 |
|---|---|---|
| 状态 | `Sold` | `Sold` |
| `transaction_buyer_id` | **null** | 买方 id |
| 进不进「我买到的」 | **不进** | 进 |

手标那条路刻意不写任何 `transaction_*` 列——**没有对手方可以记**，卖家只是把东西标记成卖出去了。所以那笔交易的买家在库里根本不存在，不可表示。一个人如果大部分成交都是手标的，他的「我买到的」就是空的。这是数据模型的边界，不是查询的毛病；有个测试专门钉着它。

### 图片

| | 值 |
|---|---|
| 每个商品上限 | **9 张**（第 10 张 409） |
| 单张上限 | **5 MB**（流式判断，不是先收完再拒） |
| 格式 | JPEG / PNG / WebP——**按解码结果定**，不是看声明的 content-type |
| 落盘 | `<content-root>/data/uploads`，按 `yyyyMM` 分目录，文件名是 GUID，**客户端给的任何东西都进不了路径** |
| 对外路径 | `/media/**`（`Storage__PublicBasePath`） |
| 派生物 | 每张图存**三个尺寸**：large 长边 1280、medium 长边 640、thumb 320×320 方裁。质量 82 |

**不保留原图。**三个尺寸都是从解码后的位图重新编码的，源字节一次都不落盘——这顺手把多格式载荷和 EXIF/GPS 一起洗掉了。所以原图里的拍摄地点从来没进过服务器。PNG 走 PNG 编码器（保住透明通道），其余走 JPEG/WebP。

### 在线人数

`status == Active && last_seen_at >= 现在 - 5 分钟`。中间件在**每个带 token 的请求**上写 `users.last_seen_at`，匿名请求一次都不写。所以这个数的意思是「最近 5 分钟有过请求的活跃账号」，**不是「此刻开着页面的人数」**——服务端没有会话，分不出两个匿名请求是不是同一个人。页脚那句说明文字就是为了这个。

## 数据库

9 张表，`utf8mb4` / `utf8mb4_0900_ai_ci`，每张表的主键都是 `id BIGINT AUTO_INCREMENT`。**列名不用逐个写 `HasColumnName`**——`AppDbContext.ApplySnakeCaseColumnNames` 在配置之后统一把 CLR 属性名转成 snake_case（`TransactionBuyerId` → `transaction_buyer_id`）。

| 表 | 装什么 | 关键约束 |
|---|---|---|
| `users` | 账号、角色、状态、重置码、最后活跃时间 | `uk_users_username`、`uk_users_student_id`（`student_id` 可空，避免历史行互撞） |
| `categories` | 分类字典，**自引用树** | `parent_id` → `categories.id` (RESTRICT)；`name` 刻意不唯一，重名在应用层挡 |
| `dormitory_areas` | 宿舍区字典 | `uk_dormitory_areas_name` |
| `products` | 商品 + **整笔交易的状态** | `price decimal(10,2)` 且有 `>= 0` 检查约束；`version` 是乐观锁并发令牌；四个外键全是 RESTRICT |
| `product_images` | 每个尺寸一列 key | `product_id` **CASCADE**——图跟着商品一起没 |
| `conversations` | 私信会话 + 冻结的商品标题/缩略图 + 待接受的提议 + **每侧的已读戳和隐藏标记** | `uk_conversations_product_id_buyer_id`（一个买家对一件商品只有一个会话） |
| `messages` | 消息正文 | `conversation_id` **CASCADE**；索引 `(conversation_id, id)` 专供倒序翻页 |
| `notifications` | 站内通知，标题正文**提前渲染好** | `related_product_id` **SET NULL**——商品硬删了通知还在，因为文本已经写死在行里 |
| `announcements` | 公告 | — |

几个值得单独说的：

- **枚举一律存 `tinyint`**（`byte`），不是字符串。所以 `status = 2` 就是「在售」。JSON 出去也是**数字**，没有注册 `JsonStringEnumConverter`——有个测试专门钉这一点。
- **软删没有全局过滤器。**`products.deleted_at` 是业务字段，靠每个查询自己显式过滤，没有 `HasQueryFilter`。
- **交易记录就是 `products` 上的四列**（`transaction_buyer_id`、`transaction_accepted_at`、`buyer_confirmed_at`、`seller_confirmed_at`），没确认的提议则是 `conversations` 上的两列。没有单独的 `transactions` 表——这是「软删而不是硬删」那条规则的全部理由。
- **`last_activity_at` / `last_message_at` 不是审计列**，是业务字段，只由各自的 service 手工推进。审计那套自动盖章不碰它们（否则「这个商品被真的改过」会被每次保存刷新，留给草稿清理的那个时钟就永远走不动）。
- 迁移是**手工执行**的（`dotnet ef database update`），启动时不建表、不改表。九个迁移按顺序：`InitialCreate` → `AddUserRoleAndMessaging` → `AddProductTransaction` → `DropImageOriginalKey` → `AddPasswordResetCode` → `AddUserLastSeenAt` → `AddUserStudentId` → `AddProductDeletedAt` → `AddConversationHide`。

## HTTP 接口

48 个控制器端点，前缀一律 `api/` + 复数 kebab-case 资源名。`{id:long}` 带内联路由约束，所以 `mine`、`bought`、`unread-count` 这种字面量段永远不会被当成 id。

| 资源 | 端点 |
|---|---|
| `api/auth` | `POST register`、`POST login`、`POST reset-password` 匿名；`GET me` 需登录 |
| `api/products` | `GET`（匿名，带筛选排序）、`GET {id}`（匿名，草稿只有卖家看得见）、`GET mine`、`GET bought`；`POST`、`PUT {id}`、`POST {id}/publish`、`POST {id}/offline`、`POST {id}/sold`、`DELETE {id}`、`POST {id}/images`、`DELETE {id}/images/{imageId}` 需登录且校验归属 |
| `api/conversations` | `GET`、`GET unread-count`、`POST`、`GET {id}`、`POST {id}/messages`、`POST {id}/read`、`DELETE {id}`、`POST {id}/transaction`、`.../transaction/accept`、`.../cancel`、`.../receipt`、`.../payment`——全需登录，且**非参与方 404**。`DELETE` 只删调用者这一侧，交易进行中回 409 |
| `api/notifications` | `GET`、`GET unread-count`、`POST {id}/read`、`DELETE {id}`、`DELETE product/{productId}`——后一个是**整组删**（一个商品的通知），幂等：匹配不到也算成功 |
| `api/categories` | `GET` 匿名；`POST` / `PUT {id}` / `DELETE {id}` 管理员 |
| `api/dormitory-areas` | `GET`、`GET {id}` 匿名；`POST` / `PUT {id}` / `DELETE {id}` 管理员 |
| `api/announcements` | `GET` 匿名；`POST` / `DELETE {id}` 管理员 |
| `api/users` | `GET`、`POST {id}/reset-password` 管理员 |
| `api/online` | `GET count`（匿名） |
| 非控制器 | `GET /health`；Development 下还有 `/openapi/v1.json`、`/scalar/v1`、`GET /dev/errors/throw/{kind}` |

管理员判定**不在特性上**：那几个控制器类上只有 `[Authorize]`，真正的管理员校验在服务层回数据库查。特性只能证明「登录了」，证明不了「是管理员」。删分类/宿舍区如果还被引用会 409。

**筛选**（`GET /api/products`，全部可选）：`q`（标题+描述模糊）、`categoryId`、`areaId`、`condition`（1..4）、`minPrice` / `maxPrice`（含端点，单位元）、`sort`（`Newest` 默认，可按名字或数字传）。

**分页**：处处都是 `page`（从 1 起）+ `pageSize`。默认 **20**，上限 **100**。越界的值是**夹紧而不是报错**——`page=0` 得到第 1 页，不返回 400；`pageSize=0` 回落到默认 20。响应里回显的是夹紧后的值。只有传了非数字才是模型绑定错误。

**错误一律是 RFC 7807 `application/problem+json`**，带两个扩展成员：`code`（该分支的稳定值）和 `traceId`（和服务端日志里那一行**是同一个值**——框架默认塞进去的 W3C trace id 在日志里根本找不到，所以被覆盖掉了）。

| `code` | 状态码 | |
|---|---|---|
| `VALIDATION_ERROR` | 400 | 请求体/查询串没绑上 |
| `BAD_REQUEST` / `INVALID_ARGUMENT` | 400 | |
| `UNAUTHORIZED` | 401 | 没带 token 或 token 不可用 |
| `INVALID_CREDENTIALS` | 401 | 用户名密码不对，或重置码无效/过期 |
| `FORBIDDEN` | 403 | 登录了但没权限 |
| `NOT_FOUND` | 404 | |
| `CONFLICT` | 409 | 唯一约束撞了 |
| `INVALID_STATE` | 409 | **状态机拒绝**，不是唯一约束——客户端可以靠这个区分 |
| `UNSUPPORTED_MEDIA_TYPE` | 415 | 图片解不出来 |
| `NOT_IMPLEMENTED` / `TIMEOUT` | 501 / 504 | |
| `INTERNAL_ERROR` | 500 | 兜底 |

## 测试

两个项目分开是有原因的，不是随手拆的。

```powershell
dotnet test                                            # 两个项目都跑
dotnet test tests/NCUT_Market.ApiTests                 # 只跑 HTTP 层
```

HTTP 层测试需要 **`NCUT_TEST_CONNECTION`** 环境变量，库名里必须含 `ncut_market_test`：

```powershell
$line = Get-Content .env -Encoding UTF8 | Where-Object { $_ -match '^ConnectionStrings__DefaultConnection=' } | Select-Object -First 1
$base = $line.Substring($line.IndexOf('=') + 1).Trim()
$env:NCUT_TEST_CONNECTION = ($base -replace 'Database=[^;]*', 'Database=ncut_market_test')
dotnet test
```

环境变量在两次 PowerShell 调用之间不保留，构造和 `dotnet test` 得写在同一条命令里。跑之前先停开发服务器，它会锁住 `bin/Debug` 的 DLL。

`tests/NCUT_Market.ApiTests` 整个程序集**串行**执行（`xunit.runner.json`）：每个测试类各有一个 `WebApplicationFactory`，并行就是好几个进程内 API 宿主同时写同一个库，而好几张表带着唯一索引。

前端那点纯函数另有检查，它住在 `out/` 下但那行是源码（`.gitignore` 里专门放行）：

```powershell
node out/check-frontend.js
```

测的是 `esc` / `formatDateTime` / `treeHtml` 这些不碰 DOM 的函数，外加一道「use strict 下有没有漏声明变量」。视图、请求和 canvas 画图只能靠手点。

## 已知边界

写下来是因为它们都是**有意的**，不是没顾上。改之前先读这一节。

- **手标售出的买家不可表示。**卖家在详情页点「标记已售出」时没有对手方可以记，所以那笔交易的买家在库里根本不存在，永远进不了「我买到的」。要修得让卖家标记时从自己的会话里挑一个买家——那等于把两条路合并成一条，是个产品决定，不是 bug fix。
- **草稿清理没实现。**`last_activity_at` 这个时钟已经在走了，删草稿的那个任务还没写。`NotificationType.DraftDeleted = 1` 是为它留的坑。
- **停用账号的既有 token 还有效。**`[Authorize]` 只验签名和有效期，普通会员接口不复查 `status`。要堵得在每个请求上回库查一次，代价是每请求一次查询。当前只在登录、重置密码、`/api/auth/me` 和管理操作上复查。
- **没有 refresh token。**签一次 7 天，过期重新登录。
- **没有 API 版本化。**`/api/**` 就是当前版本。
- **不存原图。**每张图只留 1280 / 640 / 320 三个重新编码过的尺寸，源字节从不落盘。这是隐私上的好事，但也意味着「重新裁一次缩略图」得让用户重传。
- **前端只有纯函数有自动测试。**视图、请求、canvas 画图靠手点。前端那些更细的取舍写在 [`web/README.md`](web/README.md) 里。

## 仓库镜像

同一个 `master` 推两个地方，两个远程没有主次之分：

| 远程 | 地址 |
|---|---|
| `origin` | <https://gitee.com/tian_puwen/ncut_-market> |
| `github` | <https://github.com/foolbirdtbw/NCUT_Market> |

```powershell
git push origin master
git push github master
```

页面页脚那两张图标链的就是这两个地址，换远程要连 `web/index.html` 一起改。登录页和页脚都没放凭据，推公开仓库不漏密码——`.env` 被 `.gitignore` 挡着，仓库里跟踪的只有 `.env.example` 那份 `Password=replace-me` 占位值。

## 许可证

**GPL-2.0 已经换掉了**——那个许可证限制的是「改了必须也开源」，它**允许商用**，和这里要的不是一回事。

现在是 [PolyForm Noncommercial License 1.0.0](LICENSE)：

| 可以做 | 需要单独授权 |
|---|---|
| 自己用、自己改、改完发给别人 | 任何商业用途 |
| 学习、实验、个人项目、业余爱好 | |
| 学校、慈善机构、公益组织、政府机构使用（**不论经费来源**） | |

**没有 copyleft**——别人改了不必开源，这一点和 GPL 正相反。许可证正文是官方原文，一字未改；`Required Notice` 那行按 PolyForm 的要求写在标题下面（禁商用的许可证得让人找得到你，所以那行带了邮箱）。

顺带一个吻合：`SixLabors.ImageSharp` 3.1 是「开源和个人使用免费，超过营收门槛的商用要买 key」。这个项目本身就禁商用，所以下游不存在会踩那条线的人——这也是当初没有为了绕开它去换图片库的原因。

第三方依赖各有各的许可证，不因为本项目这份而改变（jQuery MIT、Pomelo MIT、ImageSharp Six Labors Split License 等）。

## 其他文档

- [`web/README.md`](web/README.md)：前端。每个 js 模块管什么、两套主题、以及一长串**已知取舍**（为什么 `/favicon.ico` 是 404、为什么详情页不能加图、为什么删除分两种……），改动前端之前值得先读那张表。
- `/scalar`：跑起来之后的接口文档，带 token 输入框。
