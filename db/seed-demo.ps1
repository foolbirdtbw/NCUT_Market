#Requires -Version 5.1
<#
.SYNOPSIS
    给开发库造一批能点的演示数据。

.DESCRIPTION
    走真实 HTTP 接口，不直接写表。理由：密码要真哈希、图片要真经过 ImageSharp 处理
    （否则缩略图文件不存在，列表页一片灰底占位），上架有状态机，每个请求都要带 JWT。
    绕过这些等于造一批假数据去测一个假流程。

    唯一直接写库的地方有三处，都是接口覆盖不到的：
      · 分类 / 宿舍区补数据——这两张表只有 GET 接口
      · 把一个账号提成管理员——仓库既有的唯一提权方式就是一条手工 SQL
      · -Reset 清理

.PARAMETER BaseUrl
    正在运行的 API 地址。默认对齐 launchSettings 里的 http profile。

.PARAMETER Users
    造多少个普通用户。

.PARAMETER Products
    造多少个商品。

.PARAMETER Reset
    先删掉上一次造的演示数据（用户名以 demo_ 开头的账号及其一切），再重新造。
    不加这个开关的话重跑是**累加**的。

.EXAMPLE
    powershell -File db/seed-demo.ps1
.EXAMPLE
    powershell -File db/seed-demo.ps1 -Reset -Products 20

.NOTES
    需要 API 已经在跑：dotnet run --project src/NCUT_Market.Api --launch-profile http
    连接串从仓库根的 .env 读，不新增任何配置文件。
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5087",
    [int]$Users = 20,
    [int]$Products = 60,
    [switch]$Reset
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Net.Http

$BaseUrl = $BaseUrl.TrimEnd("/")

# ============================================================================
# 连接信息：读仓库根 .env 里那份本项目的连接串
# ============================================================================

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot ".env"

if (-not (Test-Path $envFile)) {
    throw "找不到 $envFile —— 这个是本项目的本地配置文件，脚本要靠它连库补字典和提权。"
}

$connLine = Get-Content $envFile | Where-Object { $_ -match '^ConnectionStrings__DefaultConnection=' } | Select-Object -First 1

if (-not $connLine) {
    throw "$envFile 里没有 ConnectionStrings__DefaultConnection。"
}

$connString = $connLine.Substring($connLine.IndexOf("=") + 1).Trim()
$conn = @{}

foreach ($pair in $connString.Split(";")) {
    $eq = $pair.IndexOf("=")

    if ($eq -gt 0) {
        $conn[$pair.Substring(0, $eq).Trim()] = $pair.Substring($eq + 1).Trim()
    }
}

$dbHost = if ($conn["Server"]) { $conn["Server"] } else { "localhost" }
$dbPort = if ($conn["Port"]) { $conn["Port"] } else { "3306" }
$dbName = $conn["Database"]
$dbUser = $conn["User"]
$dbPass = $conn["Password"]

if (-not $dbName -or -not $dbUser) {
    throw "从 .env 解析连接串失败，至少要有 Database 和 User。"
}

$mysql = "C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe"

if (-not (Test-Path $mysql)) {
    $onPath = Get-Command mysql.exe -ErrorAction SilentlyContinue

    if (-not $onPath) {
        throw "找不到 mysql 客户端。装 MySQL 8.4 或者把 mysql.exe 加进 PATH。"
    }

    $mysql = $onPath.Source
}

# 密码走环境变量而不是 -p 参数：-p 的值会出现在进程列表里。
$env:MYSQL_PWD = $dbPass

<#
    跑一段 SQL。中文要显式指定 utf8mb4，不然建出来的分类名会是乱码。
    $IncludeDatabase 为假时连 -D 都不带——建库之前是连不上的。
#>
function Invoke-Sql {
    param([string]$Sql, [switch]$Scalar)

    $args = @("--host=$dbHost", "--port=$dbPort", "--user=$dbUser", "--default-character-set=utf8mb4", "--batch", "--skip-column-names", "-D", $dbName, "--execute=$Sql")
    $output = & $mysql @args 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "SQL 执行失败：`n$($output -join "`n")`n--- SQL ---`n$Sql"
    }

    if ($Scalar) {
        return ($output | Select-Object -First 1)
    }

    return $output
}

# ============================================================================
# HTTP 客户端
#
# 全程用 HttpClient 而不是 Invoke-RestMethod：出错时要能看到响应体里的中文校验消息，
# 而 PS 5.1 的 Invoke-RestMethod 把响应体藏在异常里很难取。上传那一段也只能用
# HttpClient——PS 5.1 没有 Invoke-RestMethod -Form。
#
# ConvertTo-Json 会把中文转义成 \uXXXX，正好绕开了 Git Bash / curl 那套编码坑。
# ============================================================================

$script:Http = New-Object System.Net.Http.HttpClient
$script:Http.Timeout = [TimeSpan]::FromSeconds(120)

function Invoke-Json {
    param(
        [string]$Method,
        [string]$Path,
        $Body,
        [string]$Token,
        [int]$Expect = 200
    )

    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($Method)), "$BaseUrl$Path"

    if ($Token) {
        $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue "Bearer", $Token
    }

    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 8 -Compress
        $request.Content = New-Object System.Net.Http.StringContent $json, ([System.Text.Encoding]::UTF8), "application/json"
    }

    $response = $script:Http.SendAsync($request).GetAwaiter().GetResult()

    try {
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $status = [int]$response.StatusCode

        if ($status -ne $Expect) {
            throw "$Method $Path 返回 $status，期望 $Expect`n$text"
        }

        if ([string]::IsNullOrWhiteSpace($text)) {
            return $null
        }

        return $text | ConvertFrom-Json
    }
    finally {
        $response.Dispose()
        $request.Dispose()
    }
}

<#
    上传一张图。字段名必须是 file —— 对齐 ProductsController.AddImage 的
    IFormFile file 参数名，写错了服务端会当成没传文件。
#>
function Send-Image {
    param([long]$ProductId, [string]$FilePath, [string]$Token)

    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    $fileContent = New-Object System.Net.Http.ByteArrayContent (, $bytes)
    $fileContent.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue "image/png"

    $form = New-Object System.Net.Http.MultipartFormDataContent
    $form.Add($fileContent, "file", [System.IO.Path]::GetFileName($FilePath))

    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Post), "$BaseUrl/api/products/$ProductId/images"
    $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue "Bearer", $Token
    $request.Content = $form

    $response = $script:Http.SendAsync($request).GetAwaiter().GetResult()

    try {
        if ([int]$response.StatusCode -ne 201) {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            throw "上传图片到商品 $ProductId 返回 $([int]$response.StatusCode)`n$text"
        }
    }
    finally {
        $response.Dispose()
        $request.Dispose()
        $form.Dispose()
    }
}

# ============================================================================
# 占位图
# ============================================================================

$Palette = @(
    "#3f6fb5", "#7a4fa3", "#2f8f6f", "#b5543f", "#4a6fa5",
    "#8a6d3b", "#556b8a", "#9c4f6b", "#3d7a7a", "#6b6b3d"
)

<#
    画一张 800x800 的纯色 PNG：一个大序号加标题，最多两行。
    用 System.Drawing 而不是引入依赖——PS 5.1 自带，够用。
#>
function New-PlaceholderImage {
    param([string]$Path, [int]$Number, [string]$Title, [string]$Back)

    $size = 800
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml($Back))

        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(55, 0, 0, 0)), 6
        $graphics.DrawRectangle($pen, 3, 3, $size - 7, $size - 7)
        $pen.Dispose()

        # 大序号压得很淡，当背景纹理用；标题才是内容。
        $numberFont = New-Object System.Drawing.Font "Arial", 220, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $numberText = "{0:D3}" -f $Number
        $numberSize = $graphics.MeasureString($numberText, $numberFont)
        $numberBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(55, 255, 255, 255))
        $graphics.DrawString($numberText, $numberFont, $numberBrush, ($size - $numberSize.Width) / 2, ($size - $numberSize.Height) / 2 - 80)
        $numberBrush.Dispose()
        $numberFont.Dispose()

        $titleFont = New-Object System.Drawing.Font "Microsoft YaHei", 46, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $titleBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 255, 255, 255))

        # 超过 10 个字就切成两行。标题最长 100 字符，但这里只画前 20 个，够认出是哪件东西。
        $shown = if ($Title.Length -gt 20) { $Title.Substring(0, 20) } else { $Title }
        $lines = if ($shown.Length -gt 10) { @($shown.Substring(0, 10), $shown.Substring(10)) } else { @($shown) }

        $y = $size - 120 - ($lines.Count * 60)

        foreach ($line in $lines) {
            $lineSize = $graphics.MeasureString($line, $titleFont)
            $graphics.DrawString($line, $titleFont, $titleBrush, ($size - $lineSize.Width) / 2, $y)
            $y += 60
        }

        $titleBrush.Dispose()
        $titleFont.Dispose()

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

# ============================================================================
# 文案池
# ============================================================================

$Nicknames = @(
    "小林", "阿哲", "橙子", "老张", "小满", "阿凯", "桃子", "大鹏", "小鹿", "阿飞",
    "罐头", "柚子", "阿May", "小舟", "老李", "西西", "阿蛮", "豆子", "小柯", "阿远",
    "北风", "竹里", "阿澈", "小鱼", "老周", "南乔", "阿渡", "青禾", "小野", "阿宁"
)

# 按分类名索引。分类 id 是运行时按名字查出来的，不硬编码。
$Items = @{
    "宿舍用品" = @(
        "台灯", "衣架", "收纳箱", "床上书桌", "拖鞋", "保温杯", "电蚊香", "晾衣绳",
        "穿衣镜", "蚊帐", "床头挂袋", "小风扇", "坐垫", "雨伞", "洗衣篮"
    )
    "电子数码" = @(
        "机械键盘", "显示器", "无线鼠标", "头戴耳机", "充电宝", "蓝牙音箱", "路由器",
        "网线", "U盘", "摄像头", "手机支架", "散热底座", "手柄", "移动硬盘", "读卡器"
    )
    "教材书籍" = @(
        "高等数学", "线性代数", "大学物理", "数据结构", "计算机网络", "操作系统",
        "概率论与数理统计", "四级真题", "考研数学", "数字电路", "编译原理", "离散数学",
        "英语语法书", "经济学原理", "C语言程序设计"
    )
}

$Prefixes = @("闲置", "九成新", "自用", "毕业出", "全新未拆", "低价出", "宿舍清仓", "八成新")

$Descriptions = @(
    "用了一个学期，功能都正常，外观没什么磕碰。有意向可以约在宿舍楼下看货。",
    "买回来基本没用过，一直放着占地方。价格可以小刀，诚心要的来。",
    "毕业了带不走，低价处理。东西是好的，就是有点落灰，擦擦就行。",
    "成色如图。当时买的时候挺贵，现在便宜出，不议价了。",
    "室友搬走留给我的，我用不上。有任何问题可以随时问我。",
    "功能完好，配件齐全，原包装还在。宿舍区可以送到楼下。"
)

$MessageTexts = @(
    "你好，这个还在吗？",
    "在的，随时可以看。",
    "价格能便宜点吗？",
    "可以小刀一点，你出多少？",
    "那我要了，什么时候方便看货？",
    "明天下午都行，你定个时间。",
    "东西成色怎么样？有明显磕碰吗？",
    "基本没什么磕碰，就是正常使用痕迹。",
    "我住 3 号楼，你在哪栋？",
    "我在 2 号楼，离得挺近的。",
    "好的，那就明天下午三点楼下见。",
    "行，到时候联系。"
)

# ============================================================================
# 阶段 0：前置检查 + 字典
# ============================================================================

Write-Host ""
Write-Host "=== NCUT_Market 演示数据 ===" -ForegroundColor Cyan
Write-Host "接口 $BaseUrl · 目标库 $dbName@$dbHost`:$dbPort"
Write-Host ""

Write-Host "[0/6] 检查接口是否在跑…" -ForegroundColor Yellow

try {
    Invoke-Json "GET" "/api/categories?page=1&pageSize=100" | Out-Null
}
catch {
    throw "连不上 $BaseUrl —— 先起服务：dotnet run --project src/NCUT_Market.Api --launch-profile http`n$($_.Exception.Message)"
}

if ($Reset) {
    Write-Host "      清理上一次的演示数据…" -ForegroundColor DarkGray

    # users → products 是 Restrict，级联靠不住，只能从叶子往上删。
    # LIKE 'demo\_%' 里那个反斜杠是转义：不转义的话 _ 是单字符通配，会误伤 demoX 这种名字。
    $cleanup = @'
DELETE FROM messages
 WHERE conversation_id IN (
   SELECT id FROM conversations
    WHERE buyer_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%')
       OR seller_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%'));

DELETE FROM conversations
 WHERE buyer_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%')
    OR seller_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%');

DELETE FROM product_images
 WHERE product_id IN (SELECT id FROM products WHERE seller_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%'));

DELETE FROM products WHERE seller_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%');

-- 通知 → users 也是 Restrict，而且刻意如此：账号是被停用而不是被删的，不能让一行 user
-- 悄悄带走通知历史。这里是演示账号真的要被删掉，所以只能显式清一道。
-- 必须摆在 users 前面。
DELETE FROM notifications WHERE user_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%');

DELETE FROM users WHERE username LIKE 'demo\_%';
'@

    Invoke-Sql $cleanup | Out-Null
}

Write-Host "      补分类和宿舍区字典…" -ForegroundColor DarkGray

# 这两张表只有 GET 接口，没有创建接口，所以只能走 SQL。
# 条件式的 INSERT ... SELECT ... WHERE NOT EXISTS，重复跑不会产生重复行。
$dictionarySql = @'
INSERT INTO categories (name, parent_id, sort_order, status, created_at, updated_at)
SELECT '数码配件', p.id, 10, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
  FROM (SELECT id FROM categories WHERE name = '电子数码' AND parent_id IS NULL) p
 WHERE NOT EXISTS (SELECT 1 FROM categories c WHERE c.name = '数码配件' AND c.parent_id = p.id);

INSERT INTO categories (name, parent_id, sort_order, status, created_at, updated_at)
SELECT '影音设备', p.id, 20, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
  FROM (SELECT id FROM categories WHERE name = '电子数码' AND parent_id IS NULL) p
 WHERE NOT EXISTS (SELECT 1 FROM categories c WHERE c.name = '影音设备' AND c.parent_id = p.id);

INSERT INTO categories (name, parent_id, sort_order, status, created_at, updated_at)
SELECT '公共课教材', p.id, 10, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
  FROM (SELECT id FROM categories WHERE name = '教材书籍' AND parent_id IS NULL) p
 WHERE NOT EXISTS (SELECT 1 FROM categories c WHERE c.name = '公共课教材' AND c.parent_id = p.id);

INSERT INTO categories (name, parent_id, sort_order, status, created_at, updated_at)
SELECT '专业课教材', p.id, 20, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
  FROM (SELECT id FROM categories WHERE name = '教材书籍' AND parent_id IS NULL) p
 WHERE NOT EXISTS (SELECT 1 FROM categories c WHERE c.name = '专业课教材' AND c.parent_id = p.id);

INSERT INTO categories (name, parent_id, sort_order, status, created_at, updated_at)
SELECT '床品', p.id, 10, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
  FROM (SELECT id FROM categories WHERE name = '宿舍用品' AND parent_id IS NULL) p
 WHERE NOT EXISTS (SELECT 1 FROM categories c WHERE c.name = '床品' AND c.parent_id = p.id);

INSERT INTO dormitory_areas (name, sort_order, status, created_at, updated_at)
SELECT '4号楼', 40, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
 WHERE NOT EXISTS (SELECT 1 FROM dormitory_areas WHERE name = '4号楼');

INSERT INTO dormitory_areas (name, sort_order, status, created_at, updated_at)
SELECT '5号楼', 50, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
 WHERE NOT EXISTS (SELECT 1 FROM dormitory_areas WHERE name = '5号楼');

INSERT INTO dormitory_areas (name, sort_order, status, created_at, updated_at)
SELECT '6号楼', 60, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR
 WHERE NOT EXISTS (SELECT 1 FROM dormitory_areas WHERE name = '6号楼');
'@

# 所有时间戳写成 UTC_TIMESTAMP(3) + INTERVAL 8 HOUR 而不是 NOW(3)：
# 这个 MySQL 实例钉在 UTC，而列里存的是北京时间（AppDbContext.AuditNow 的口径），
# NOW(3) 会整整差 8 小时。这条纪律来自 db/dev-seed.sql 的注释。
Invoke-Sql $dictionarySql | Out-Null

# 按名字查 id，不硬编码——库里的 id 取决于 dev-seed.sql 是什么时候跑的。
$categoryRows = Invoke-Sql "SELECT c.name, c.id FROM categories c WHERE c.parent_id IS NULL ORDER BY c.sort_order;"
$areaRows = Invoke-Sql "SELECT name, id FROM dormitory_areas WHERE status = 1 ORDER BY sort_order;"

$categories = @()

foreach ($row in $categoryRows) {
    $parts = $row -split "`t"

    if ($parts.Count -ge 2 -and $Items.ContainsKey($parts[0])) {
        $categories += @{ Name = $parts[0]; Id = [long]$parts[1] }
    }
}

if ($categories.Count -eq 0) {
    throw "一个可用的顶层分类都没查到。先跑一遍 db/dev-seed.sql。"
}

$areas = @()

foreach ($row in $areaRows) {
    $parts = $row -split "`t"

    if ($parts.Count -ge 2) {
        $areas += @{ Name = $parts[0]; Id = [long]$parts[1] }
    }
}

if ($areas.Count -eq 0) {
    throw "一个可用的宿舍区都没查到。先跑一遍 db/dev-seed.sql。"
}

Write-Host "      分类 $($categories.Count) 个顶层、宿舍区 $($areas.Count) 个" -ForegroundColor DarkGray

# ============================================================================
# 阶段 1：用户
# ============================================================================

Write-Host "[1/6] 注册 $Users 个用户…" -ForegroundColor Yellow

$Password = "Demo12345!"

function New-DemoUsername {
    $suffix = -join (1..8 | ForEach-Object { "0123456789abcdefghijklmnopqrstuvwxyz"[(Get-Random -Maximum 36)] })

    return "demo_$suffix"
}

# 注册要求学号唯一，所以每次都得是新号：20 + 11 位随机数字，正好是服务端正则要的 13 位。
# 不加 -Reset 重跑是累加的，随机撞号的概率在几百个账号上是 n²/2·10¹¹，不管它。
function New-DemoStudentId {
    return "20" + (Get-Random -Minimum 10000000000 -Maximum 99999999999)
}

$accounts = @()

foreach ($index in 1..$Users) {
    $username = New-DemoUsername
    $nickname = $Nicknames[(Get-Random -Maximum $Nicknames.Count)]

    # 注册返回 200 而不是 201：它直接给 token，不是「创建了资源+Location」那套。
    $result = Invoke-Json "POST" "/api/auth/register" @{
        username  = $username
        password  = $Password
        nickname  = $nickname
        studentId = New-DemoStudentId
    } -Expect 200

    $accounts += @{
        Id       = [long]$result.user.id
        Username = $username
        Nickname = $nickname
        Token    = $result.token
    }

    Write-Progress -Activity "注册用户" -Status $username -PercentComplete (($index / $Users) * 100)
}

Write-Progress -Activity "注册用户" -Completed

Write-Host "      管理员账号 demo_admin…" -ForegroundColor DarkGray

$adminName = New-DemoUsername
$adminResult = Invoke-Json "POST" "/api/auth/register" @{
    username  = $adminName
    password  = $Password
    nickname  = "站务"
    studentId = New-DemoStudentId
} -Expect 200

$admin = @{
    Id       = [long]$adminResult.user.id
    Username = $adminName
    Nickname = "站务"
    Token    = $adminResult.token
}

# 仓库里唯一的提权方式就是这条 SQL，没有接口，也没有降权。
Invoke-Sql "UPDATE users SET role = 2, updated_at = UTC_TIMESTAMP(3) + INTERVAL 8 HOUR WHERE username = '$adminName';" | Out-Null

# 提权是直接改库的，手里这个 token 是提权前签发的。作者在方案里说过 role 不进 token、
# 每次管理操作现查库，所以同一个 token 立刻就能发公告——这里不重新登录，正好顺带验证这一点。

# ============================================================================
# 阶段 2：商品
# ============================================================================

Write-Host "[2/6] 发布 $Products 个商品…" -ForegroundColor Yellow

$tempDir = Join-Path $env:TEMP ("ncut-seed-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$published = @()   # 只收集真正上架的，后面的会话要挂在它们上面
$imageNumber = 0

for ($index = 1; $index -le $Products; $index++) {
    $account = $accounts[(Get-Random -Maximum $accounts.Count)]
    $category = $categories[(Get-Random -Maximum $categories.Count)]
    $area = $areas[(Get-Random -Maximum $areas.Count)]

    $item = $Items[$category.Name][(Get-Random -Maximum $Items[$category.Name].Count)]
    $title = "$($Prefixes[(Get-Random -Maximum $Prefixes.Count)]) $item"

    # 价格分档：书便宜，数码贵，宿舍用品中间。
    $price = switch ($category.Name) {
        "教材书籍" { Get-Random -Minimum 5 -Maximum 45 }
        "电子数码" { Get-Random -Minimum 40 -Maximum 900 }
        default { Get-Random -Minimum 8 -Maximum 120 }
    }

    $created = Invoke-Json "POST" "/api/products" @{
        title           = $title
        description     = $Descriptions[(Get-Random -Maximum $Descriptions.Count)]
        price           = $price
        condition       = (Get-Random -Minimum 1 -Maximum 5)
        categoryId      = $category.Id
        dormitoryAreaId = $area.Id
    } -Token $account.Token -Expect 201

    $productId = [long]$created.id

    # 一到三张图。真的走一遍 ImageSharp，缩略图文件才会存在。
    $imageCount = Get-Random -Minimum 1 -Maximum 4

    for ($n = 1; $n -le $imageCount; $n++) {
        $imageNumber++
        $file = Join-Path $tempDir "p$imageNumber.png"

        New-PlaceholderImage -Path $file -Number $imageNumber -Title $title -Back $Palette[(Get-Random -Maximum $Palette.Count)]
        Send-Image -ProductId $productId -FilePath $file -Token $account.Token

        Remove-Item $file -Force
    }

    # 状态分布：约 70% 在售、10% 已售出、10% 已下架、10% 草稿。
    # 草稿不发布，所以它没有 publish 这一步——正好覆盖「不传图也能存草稿」之外的路径。
    $roll = Get-Random -Minimum 1 -Maximum 101

    if ($roll -le 70) {
        Invoke-Json "POST" "/api/products/$productId/publish" -Token $account.Token | Out-Null
        $published += @{ Id = $productId; SellerId = $account.Id; Title = $title }
    }
    elseif ($roll -le 80) {
        Invoke-Json "POST" "/api/products/$productId/publish" -Token $account.Token | Out-Null
        Invoke-Json "POST" "/api/products/$productId/sold" -Token $account.Token | Out-Null
        # 已售出的不进 $published：会话要挂在能被人看见的在售商品上。
    }
    elseif ($roll -le 90) {
        Invoke-Json "POST" "/api/products/$productId/publish" -Token $account.Token | Out-Null
        Invoke-Json "POST" "/api/products/$productId/offline" -Token $account.Token | Out-Null
    }
    # else：留在草稿

    Write-Progress -Activity "发布商品" -Status $title -PercentComplete (($index / $Products) * 100)
}

Write-Progress -Activity "发布商品" -Completed

Write-Host "      在售 $($published.Count) 个" -ForegroundColor DarkGray
Write-Host "      图片 $imageNumber 张" -ForegroundColor DarkGray

# ============================================================================
# 阶段 3：会话
# ============================================================================

Write-Host "[3/6] 造会话和消息…" -ForegroundColor Yellow

$tokenById = @{}

foreach ($account in $accounts) {
    $tokenById[$account.Id] = $account.Token
}

# 目标约 25 条会话。上架商品不够就按实际数量来。
$threadCount = [Math]::Min(25, $published.Count)
$seenConversations = New-Object System.Collections.Generic.HashSet[long]
$messageCount = 0

for ($index = 1; $index -le $threadCount; $index++) {
    # 随机挑一个在售商品，下面的 $buyers 保证买家不是卖家自己。
    $target = $published[(Get-Random -Maximum $published.Count)]

    $buyers = $accounts | Where-Object { $_.Id -ne $target.SellerId }

    if ($buyers.Count -eq 0) {
        continue
    }

    $buyer = $buyers[(Get-Random -Maximum $buyers.Count)]

    # find-or-create，返回 200 而不是 201：重复点「联系卖家」拿回来的是同一条。
    $conversation = Invoke-Json "POST" "/api/conversations" @{ productId = $target.Id } -Token $buyer.Token -Expect 200
    $conversationId = [long]$conversation.id

    # 商品和买家都是随机挑的，理论上会撞上已经建过的组合，那样拿回来的是同一条会话。
    # 去重之后汇报的数字才是真的。
    if (-not $seenConversations.Add($conversationId)) {
        continue
    }

    # 一半的会话只留一条买家消息，不读——这样卖家的未读徽标才有数字可看。
    $turns = if ($index % 2 -eq 0) { 2 + (Get-Random -Maximum 6) } else { 1 }

    for ($turn = 0; $turn -lt $turns; $turn++) {
        # 偶数轮买家说，奇数轮卖家回。
        $sender = if ($turn % 2 -eq 0) { $buyer } else { $accounts | Where-Object { $_.Id -eq $target.SellerId } | Select-Object -First 1 }

        if (-not $sender) {
            break
        }

        Invoke-Json "POST" "/api/conversations/$conversationId/messages" @{
            content = $MessageTexts[$turn % $MessageTexts.Count]
        } -Token $sender.Token -Expect 201 | Out-Null

        $messageCount++
    }

    Write-Progress -Activity "造会话" -Status "第 $index 条" -PercentComplete (($index / $threadCount) * 100)
}

Write-Progress -Activity "造会话" -Completed

# ============================================================================
# 阶段 4：公告
# ============================================================================

Write-Host "[4/6] 发布公告…" -ForegroundColor Yellow

$announcements = @(
    @{
        title   = "开学季交易提醒"
        content = "近期交易量较大，请务必当面验货、当面付款。`n`n不要提前转账，不要扫来历不明的收款码。遇到可疑情况请联系宿管或站务。"
        expiredAt = $null
    },
    @{
        title   = "国庆假期值班安排"
        content = "10 月 1 日至 7 日站务轮休，公告审核可能延迟。`n`n假期期间的交易纠纷请在假期结束后反馈。"
        # 过期时间必须是将来的，service 会拒绝过去的。写成北京时间墙上时钟、不带时区后缀——
        # 和 datetime-local 提交的格式一致，也是这个库里所有时间列的口径。
        expiredAt = [DateTime]::UtcNow.AddHours(8).AddDays(7).ToString("yyyy-MM-ddTHH:mm:ss")
    }
)

foreach ($item in $announcements) {
    Invoke-Json "POST" "/api/announcements" $item -Token $admin.Token -Expect 201 | Out-Null
}

# ============================================================================
# 阶段 5：汇总
# ============================================================================

Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "[5/6] 核对数据…" -ForegroundColor Yellow

$counts = @{}

foreach ($table in @("users", "products", "product_images", "conversations", "messages", "announcements", "categories", "dormitory_areas")) {
    $counts[$table] = [int](Invoke-Sql "SELECT COUNT(*) FROM $table;" -Scalar)
}

$statusCounts = Invoke-Sql "SELECT status, COUNT(*) FROM products WHERE seller_id IN (SELECT id FROM users WHERE username LIKE 'demo\_%') GROUP BY status ORDER BY status;"

Write-Host "[6/6] 完成。" -ForegroundColor Green
Write-Host ""

Write-Host "库内总计" -ForegroundColor Cyan
foreach ($table in @("users", "products", "product_images", "conversations", "messages", "announcements", "categories", "dormitory_areas")) {
    Write-Host ("  {0,-18} {1,6}" -f $table, $counts[$table])
}

$statusNames = @{ "1" = "草稿"; "2" = "在售"; "3" = "已售出"; "4" = "已下架" }

Write-Host ""
Write-Host "本次写入" -ForegroundColor Cyan
Write-Host ("  用户 {0} 个（含 1 个管理员）· 商品 {1} 个 · 图片 {2} 张 · 会话 {3} 条 · 消息 {4} 条" -f `
        ($accounts.Count + 1), $Products, $imageNumber, $seenConversations.Count, $messageCount)
Write-Host ""

Write-Host "本次造的商品（按状态）" -ForegroundColor Cyan
foreach ($row in $statusCounts) {
    $parts = $row -split "`t"

    if ($parts.Count -ge 2) {
        $label = if ($statusNames[$parts[0]]) { $statusNames[$parts[0]] } else { "未知($($parts[0]))" }
        Write-Host ("  {0,-8} {1,4}" -f $label, $parts[1])
    }
}

Write-Host ""
Write-Host "登录用账号" -ForegroundColor Cyan
Write-Host "  密码一律是 $Password"
Write-Host ""
Write-Host ("  管理员   {0}  ({1})" -f $admin.Username, $admin.Nickname) -ForegroundColor Yellow
Write-Host ""

foreach ($account in $accounts | Select-Object -First 5) {
    Write-Host ("  普通用户 {0}  ({1})" -f $account.Username, $account.Nickname)
}

if ($accounts.Count -gt 5) {
    Write-Host "  …… 另有 $($accounts.Count - 5) 个，见下方完整列表" -ForegroundColor DarkGray
}

# 全量写到文件，方便反复复制——20 个随机用户名从终端里抄不现实。
$accountFile = Join-Path $PSScriptRoot "demo-accounts.txt"

$lines = @(
    "# seed-demo.ps1 造的账号。这个文件是生成物，不要提交。",
    "# 密码一律 $Password",
    "",
    "admin`t$($admin.Username)`t$($admin.Nickname)"
)

foreach ($account in $accounts) {
    $lines += "user`t$($account.Username)`t$($account.Nickname)"
}

# UTF8Encoding($false) 不写 BOM：带 BOM 的话第一行会多出三个看不见的字节。
[System.IO.File]::WriteAllLines($accountFile, $lines, (New-Object System.Text.UTF8Encoding $false))

Write-Host ""
Write-Host "完整账号列表写到 $accountFile" -ForegroundColor DarkGray
Write-Host ""
