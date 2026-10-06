using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NCUT_Market.Core.DTOs.Auth;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Jobs;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Security;
using NCUT_Market.Infrastructure.Storage;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Boots the API in-process against the test database.
/// </summary>
/// <remarks>
/// <para>
/// The connection string is <em>replaced</em> rather than overridden. Setting it through
/// <c>UseSetting</c> or an environment variable does not work here: Program.cs appends the .env
/// dictionary as a configuration source after the host's own, and a source added later wins, so
/// <c>.env</c> beats the environment. Worse, <c>DotEnvLoader</c> walks up from the current
/// directory, which under a test host finds the repository root and loads the developer's real
/// .env — so the development connection string arrives whether it is wanted or not. Dropping the
/// DbContext registration and adding a new one sidesteps the whole ordering question.
/// </para>
/// <para>
/// The JWT key and upload root go through <c>PostConfigure</c>, which the options framework
/// guarantees runs after every <c>Configure</c> call regardless of who registered them. That is
/// only possible because the bearer handler builds its validation parameters lazily from
/// <c>IOptions&lt;JwtOptions&gt;</c> instead of capturing them inline — see ConfigureJwtBearerOptions.
/// </para>
/// </remarks>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Environment variable holding the test database connection string.</summary>
    public const string ConnectionStringVariable = "NCUT_TEST_CONNECTION";

    /// <summary>
    /// Substring the connection string must contain. A guard against pointing these tests at the
    /// development database, which they would happily write to and never clean up.
    /// </summary>
    public const string RequiredDatabaseMarker = "ncut_market_test";

    /// <summary>
    /// Signing key used by the test host. A fixed, publicly-known value, which is fine because it
    /// only ever signs tokens for a database that is dropped between runs — the point of
    /// substituting it is that tests never depend on, or learn, the developer's real key.
    /// </summary>
    private const string TestSigningKey = "dGVzdC1vbmx5LXNpZ25pbmcta2V5LW5vdC1mb3ItYW55LXJlYWwtdXNl";

    private readonly string _uploadRoot = Path.Combine(
        Path.GetTempPath(),
        "ncut-api-tests",
        Guid.NewGuid().ToString("N"));

    public ApiFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? throw new InvalidOperationException(
                $"Set {ConnectionStringVariable} to a MySQL connection string whose database name " +
                $"contains '{RequiredDatabaseMarker}' before running these tests.");

        if (!ConnectionString.Contains(RequiredDatabaseMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} must name a database containing " +
                $"'{RequiredDatabaseMarker}'. These tests write rows and never clean them up.");
        }
    }

    /// <summary>The connection string the test host is using.</summary>
    public string ConnectionString { get; }

    /// <summary>Top-level category. Nothing is listed in it directly — children hold the listings.</summary>
    public long RootCategoryId { get; private set; }

    /// <summary>Active child of <see cref="RootCategoryId"/>, for the "filtering includes descendants" test.</summary>
    public long ChildCategoryId { get; private set; }

    /// <summary>A second active top-level category, for tests that need a listing to not match.</summary>
    public long OtherCategoryId { get; private set; }

    /// <summary>Active dormitory area.</summary>
    public long DormitoryAreaId { get; private set; }

    /// <summary>A second active dormitory area, so a filter can be shown to exclude something.</summary>
    public long OtherDormitoryAreaId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<AppDbContext>();

            services.AddDbContext<AppDbContext>(options =>
                options.UseMySql(
                    ConnectionString,
                    new MySqlServerVersion(new Version(8, 4, 0)),
                    mysqlOptions => mysqlOptions
                        .MigrationsAssembly(typeof(AppDbContext).Assembly.FullName!)
                        .MigrationsHistoryTable("__efmigrationshistory")));

            services.PostConfigure<JwtOptions>(options =>
            {
                options.SigningKey = TestSigningKey;
                options.Issuer = "ncut-market-tests";
                options.Audience = "ncut-market-tests";
            });

            // Under the OS temp directory, so a run never leaves images inside the repository.
            services.PostConfigure<StorageOptions>(options => options.UploadRoot = _uploadRoot);

            // Required, not a nicety. Program.cs really runs under this fixture, so AddHostedService
            // really starts the sweep timer — against this shared, never-cleaned database, on every
            // test class. A sweep firing mid-test would roll back trades the test is halfway through
            // asserting on, and the failure would look like a logic bug in the service.
            services.PostConfigure<BackgroundJobsOptions>(options => options.Enabled = false);
        });
    }

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Migrate rather than EnsureCreated: the schema has to be the one the migrations describe,
        // including the check constraints and index names the model tests assert on.
        await dbContext.Database.MigrateAsync();

        await EnsureReferenceDataAsync(dbContext);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Disposes the test host. The uploaded files are left for the OS to clean up; they are in
        // a per-run temp directory and nothing reads them after the process exits.
        await base.DisposeAsync();
    }

    /// <summary>
    /// Creates a client and registers a brand-new account, returning the client with its
    /// credentials attached.
    /// </summary>
    /// <remarks>
    /// Every test gets its own account with a random suffix. Rows are never cleaned up — the test
    /// database accumulates — so uniqueness has to come from the data, exactly as the DbContext-level
    /// tests do it.
    /// </remarks>
    public async Task<(HttpClient Client, AuthResponse Auth)> CreateSignedInClientAsync()
    {
        var client = CreateClient();

        var request = new RegisterRequest(
            "user-" + Guid.NewGuid().ToString("N")[..12],
            "test-password-123",
            "测试用户");

        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new InvalidOperationException("Registration returned no body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        return (client, auth);
    }

    /// <summary>
    /// A client with no credentials.
    /// </summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>
    /// Makes sure the category tree and dormitory area the product tests list against exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The migrations create these two tables empty — there is no seeder in the project — so without
    /// this every create-listing call would fail on an unknown category id and the product tests
    /// would be testing the error path.
    /// </para>
    /// <para>
    /// Find-or-create by fixed name rather than insert, because each test class gets its own fixture
    /// against one shared database. <c>dormitory_areas.name</c> carries a unique index, so a second
    /// class inserting unconditionally would collide outright; <c>categories.name</c> does not, and
    /// would instead pile up rows the other tests then have to step around. Rows are named so they
    /// are recognisable in the test database afterwards.
    /// </para>
    /// </remarks>
    private async Task EnsureReferenceDataAsync(AppDbContext dbContext)
    {
        var root = await dbContext.Categories.FirstOrDefaultAsync(x => x.Name == "测试-根分类");

        if (root is null)
        {
            root = new Category { Name = "测试-根分类", SortOrder = 900 };
            dbContext.Categories.Add(root);
            await dbContext.SaveChangesAsync();
        }

        RootCategoryId = root.Id;

        ChildCategoryId = await EnsureCategoryAsync(dbContext, "测试-子分类", root.Id);
        OtherCategoryId = await EnsureCategoryAsync(dbContext, "测试-独立分类", null);

        DormitoryAreaId = await EnsureAreaAsync(dbContext, "测试-宿舍区");
        OtherDormitoryAreaId = await EnsureAreaAsync(dbContext, "测试-另一个宿舍区");
    }

    /// <summary>
    /// Grants an account the admin role, the way an operator would: directly in the database.
    /// </summary>
    /// <param name="userId">The account to promote.</param>
    /// <remarks>
    /// There is no endpoint for this, and there is deliberately not meant to be one — promoting an
    /// account in production is a hand-run UPDATE, so the tests do the same rather than exercising a
    /// path that does not exist. Writing through the change tracker rather than
    /// <c>ExecuteUpdateAsync</c> keeps the audit stamping intact, which the bulk API would bypass.
    /// </remarks>
    public async Task PromoteToAdminAsync(long userId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users.FirstAsync(x => x.Id == userId);
        user.Role = UserRole.Admin;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Writes an announcement row directly, for the states the API cannot produce.
    /// </summary>
    /// <param name="title">Headline. Give it a unique value — the tests assert on the title.</param>
    /// <param name="status">Lifecycle state.</param>
    /// <param name="publishedAt">When it went live, or null for never.</param>
    /// <param name="expiredAt">When it stops showing, or null for never.</param>
    /// <returns>The new row's id.</returns>
    /// <remarks>
    /// The API only ever publishes immediately, so drafts, future-dated rows and already-expired ones
    /// have to be planted to test the "live" filter at all. Timestamps are passed in as Beijing
    /// wall-clock, the same convention the columns hold.
    /// </remarks>
    public async Task<long> SeedAnnouncementAsync(
        string title,
        AnnouncementStatus status,
        DateTime? publishedAt,
        DateTime? expiredAt)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var announcement = new Announcement
        {
            Title = title,
            Content = title + " 的正文",
            Status = status,
            PublishedAt = publishedAt,
            ExpiredAt = expiredAt
        };

        dbContext.Announcements.Add(announcement);
        await dbContext.SaveChangesAsync();

        return announcement.Id;
    }

    /// <summary>
    /// Reads a listing's <c>LastActivityAt</c> straight from the database.
    /// </summary>
    /// <remarks>
    /// Not on any response DTO, deliberately — it is the future draft-cleanup job's clock, not
    /// something a client has an opinion about. Read through a fresh scope so it reflects what was
    /// committed rather than what the API's own context happens to be tracking.
    /// </remarks>
    public async Task<DateTime> LastActivityAtAsync(long productId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Products
            .Where(x => x.Id == productId)
            .Select(x => x.LastActivityAt)
            .FirstAsync();
    }

    /// <summary>
    /// Reads an image row's three storage keys, in the order large/medium/thumbnail.
    /// </summary>
    public async Task<string[]> ImageKeysAsync(long imageId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var image = await dbContext.ProductImages
            .Where(x => x.Id == imageId)
            .Select(x => new { x.LargeKey, x.MediumKey, x.ThumbnailKey })
            .FirstAsync();

        return [image.LargeKey, image.MediumKey, image.ThumbnailKey];
    }

    /// <summary>
    /// Moves a listing's trade start into the past, so a deadline the API enforces in days can be
    /// crossed without waiting for one.
    /// </summary>
    /// <param name="productId">The listing in a trade.</param>
    /// <param name="age">How far back to push <c>transaction_accepted_at</c>.</param>
    /// <remarks>
    /// Writes through the change tracker, so the audit pass runs and the concurrency token advances
    /// exactly as it would for a real write. <c>Modified</c> is the only state that bumps it; an
    /// <c>ExecuteUpdateAsync</c> here would leave the token stale and quietly break the very race the
    /// service's <c>WHERE</c> clause depends on.
    /// </remarks>
    public async Task AgeTransactionAsync(long productId, TimeSpan age)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products.FirstAsync(x => x.Id == productId);
        product.TransactionAcceptedAt = AppDbContext.AuditNow - age;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>Moves a thread's trade proposal into the past, so its one-day deadline has passed.</summary>
    /// <param name="conversationId">The thread holding the proposal.</param>
    /// <param name="age">How far back to push <c>transaction_proposed_at</c>.</param>
    public async Task AgeProposalAsync(long conversationId, TimeSpan age)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await dbContext.Conversations.FirstAsync(x => x.Id == conversationId);
        conversation.TransactionProposedAt = AppDbContext.AuditNow - age;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Moves a thread's last activity into the past.
    /// </summary>
    /// <param name="conversationId">The thread to age.</param>
    /// <param name="age">How far back to push <c>last_message_at</c>.</param>
    /// <remarks>
    /// The detail page's "recently interested" count reads this column, and there is no request that
    /// can produce a week-old thread — every one the API creates is stamped now. Planting it is the
    /// only way to exercise the cutoff.
    /// </remarks>
    public async Task AgeConversationAsync(long conversationId, TimeSpan age)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await dbContext.Conversations.FirstAsync(x => x.Id == conversationId);
        conversation.LastMessageAt = AppDbContext.AuditNow - age;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Rewrites an account's nickname.
    /// </summary>
    /// <param name="userId">The account to rename.</param>
    /// <param name="nickname">The new nickname.</param>
    /// <remarks>
    /// There is no endpoint that changes a nickname, and the fixture registers every account as 测试用户 —
    /// so a search by nickname would match the whole database. Giving the one account under test a unique
    /// nickname is what makes "the keyword found <em>this</em> account" an assertion instead of a filter.
    /// </remarks>
    public async Task RenameAsync(long userId, string nickname)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users.FirstAsync(x => x.Id == userId);
        user.Nickname = nickname;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Disables an account, the way an operator would: directly in the database.
    /// </summary>
    /// <param name="userId">The account to disable.</param>
    /// <remarks>
    /// <c>UserStatus.Disabled</c> is not reachable through the API — there is no endpoint that blocks an
    /// account, only a column that says whether it is blocked. So the two places that read it, login and
    /// reset, can only be exercised by planting it, exactly as the trade tests plant old timestamps.
    /// </remarks>
    public async Task DisableAccountAsync(long userId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users.FirstAsync(x => x.Id == userId);
        user.Status = UserStatus.Disabled;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Moves an account's outstanding reset code into the past, so a 24-hour lifetime can be crossed
    /// without waiting a day.
    /// </summary>
    /// <param name="userId">The account holding the code.</param>
    /// <param name="age">How far back to push <c>password_reset_expires_at</c>.</param>
    /// <remarks>
    /// There is no request that produces an expired code — the API only ever mints one that is good for
    /// a day — so planting it is the only way to exercise the expiry branch at all.
    /// </remarks>
    public async Task AgeResetCodeAsync(long userId, TimeSpan age)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users.FirstAsync(x => x.Id == userId);
        user.PasswordResetExpiresAt = AppDbContext.AuditNow - age;

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// An account's reset columns, read straight from the database.
    /// </summary>
    /// <remarks>
    /// The digest is not on any response DTO and deliberately never will be, so a test asserting that a
    /// redeemed or reissued code cleared the column has to look at the row. Read through a fresh scope
    /// so it reflects what was committed rather than what the API's own context is tracking.
    /// </remarks>
    public async Task<(string? Code, DateTime? ExpiresAt)> ResetCodeStateAsync(long userId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.PasswordResetCode, x.PasswordResetExpiresAt })
            .FirstAsync();

        return (user.PasswordResetCode, user.PasswordResetExpiresAt);
    }

    /// <summary>
    /// A listing's raw trade columns, read straight from the database.
    /// </summary>
    /// <remarks>
    /// Not on any response DTO — the public detail projection deliberately omits the counterparty,
    /// and none of the trade timestamps appear on the thread response except the ones belonging to
    /// the caller's own trade. Tests asserting that a rollback cleared everything, or that a specific
    /// buyer won, have to look at the row.
    /// </remarks>
    public async Task<(ProductStatus Status, long? BuyerId, DateTime? AcceptedAt, DateTime? BuyerConfirmedAt, DateTime? SellerConfirmedAt, uint Version)>
        TradeStateAsync(long productId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products
            .AsNoTracking()
            .Where(x => x.Id == productId)
            .Select(x => new
            {
                x.Status,
                x.TransactionBuyerId,
                x.TransactionAcceptedAt,
                x.BuyerConfirmedAt,
                x.SellerConfirmedAt,
                x.Version
            })
            .FirstAsync();

        return (product.Status, product.TransactionBuyerId, product.TransactionAcceptedAt,
            product.BuyerConfirmedAt, product.SellerConfirmedAt, product.Version);
    }

    /// <summary>A thread's proposal columns, read straight from the database.</summary>
    public async Task<(long? ProposedById, DateTime? ProposedAt)> ProposalStateAsync(long conversationId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .Where(x => x.Id == conversationId)
            .Select(x => new { x.TransactionProposedById, x.TransactionProposedAt })
            .FirstAsync();

        return (conversation.TransactionProposedById, conversation.TransactionProposedAt);
    }

    /// <summary>
    /// An account's <c>last_seen_at</c>, read straight from the database.
    /// </summary>
    /// <remarks>
    /// Read through a fresh scope with <c>AsNoTracking</c>, so this reflects what was committed rather
    /// than what any context the API is holding might be tracking. The column is written by
    /// <c>ExecuteUpdateAsync</c>, which no context tracks at all — the point of asserting here rather
    /// than through a response is that the write is invisible to everything except the row.
    /// </remarks>
    public async Task<DateTime?> LastSeenAtAsync(long userId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.LastSeenAt)
            .FirstAsync();
    }

    /// <summary>
    /// An account's <c>updated_at</c>, read straight from the database.
    /// </summary>
    /// <remarks>
    /// The audit column, and the thing the presence heartbeat must not disturb: presence is recorded
    /// through <c>ExecuteUpdateAsync</c> precisely so that it skips the unconditional stamp that
    /// <c>AppDbContext</c> applies to anything the change tracker sees as modified.
    /// </remarks>
    public async Task<DateTime> UpdatedAtAsync(long userId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.UpdatedAt)
            .FirstAsync();
    }

    /// <summary>
    /// Plants a block of accounts whose presence readings are known, and returns their ids.
    /// </summary>
    /// <param name="count">How many accounts to add.</param>
    /// <param name="idleFor">
    /// How long ago each account was last seen — <see cref="TimeSpan.Zero"/> for "just now",
    /// something past the window for "has fallen out". <c>null</c> leaves the column unset, which is
    /// the state a registered account is in before its first request.
    /// </param>
    /// <param name="status">Whether the accounts can do anything, which is not the same as being seen.</param>
    /// <remarks>
    /// <para>
    /// Rows inserted straight into the table rather than accounts registered through the API. There is
    /// no endpoint that blocks an account or backdates one, so those states can only be planted — and
    /// going through <c>/api/auth/register</c> would mean one deliberate bcrypt hash per row, which is
    /// the wrong thing to spend a second of every test run on for an account nobody signs in as.
    /// </para>
    /// <para>
    /// The block exists because these tests count. The test database is shared by every class and never
    /// cleaned, so a number read from it is a number plus whatever the rest of the suite stamped in the
    /// meantime. Planting thirty rows the test owns makes its own contribution the signal and everyone
    /// else's the noise, which is the only arrangement in which a count is worth asserting on.
    /// </para>
    /// </remarks>
    public async Task<long[]> SeedAccountsAsync(
        int count,
        TimeSpan? idleFor = null,
        UserStatus status = UserStatus.Active)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var now = AppDbContext.AuditNow;

        var users = Enumerable.Range(0, count)
            .Select(index => new User
            {
                Username = $"planted-{suffix}-{index}",
                PasswordHash = "not-a-password-hash",
                Nickname = "planted",
                Status = status,
                LastSeenAt = idleFor is null ? null : now - idleFor,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();

        dbContext.Users.AddRange(users);

        await dbContext.SaveChangesAsync();

        return [.. users.Select(x => x.Id)];
    }

    /// <summary>
    /// Clears every account's presence reading, so a counting test can start from a known baseline.
    /// </summary>
    /// <remarks>
    /// The test database is shared by every test class and never cleaned, and xUnit runs those classes
    /// in parallel, so "nobody is online" is not a state any test can assume — it has to be created.
    /// Nothing else in the suite reads this column, so clearing it cannot perturb another class.
    /// </remarks>
    public async Task ClearLastSeenAsync()
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Users.ExecuteUpdateAsync(
            setters => setters.SetProperty(x => x.LastSeenAt, (DateTime?)null));
    }

    /// <summary>
    /// The online count, computed by the running application rather than by the test.
    /// </summary>
    /// <remarks>
    /// Deliberately the real service and not a re-implementation of its query. A copy here would
    /// duplicate the window constant, and the two would drift apart silently in the direction that
    /// keeps the tests green. Reading it this way makes the HTTP call the only thing under test.
    /// </remarks>
    public async Task<int> CountOnlineAsync()
    {
        await using var scope = Services.CreateAsyncScope();

        var onlineService = scope.ServiceProvider.GetRequiredService<IOnlineService>();

        return await onlineService.CountAsync();
    }

    private static async Task<long> EnsureCategoryAsync(AppDbContext dbContext, string name, long? parentId)
    {
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Name == name);

        if (category is null)
        {
            category = new Category { Name = name, ParentId = parentId, SortOrder = 900 };
            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();
        }

        return category.Id;
    }

    private static async Task<long> EnsureAreaAsync(AppDbContext dbContext, string name)
    {
        var area = await dbContext.DormitoryAreas.FirstOrDefaultAsync(x => x.Name == name);

        if (area is null)
        {
            area = new DormitoryArea { Name = name, SortOrder = 900 };
            dbContext.DormitoryAreas.Add(area);
            await dbContext.SaveChangesAsync();
        }

        return area.Id;
    }

    /// <summary>
    /// The JSON options the API serializes with, for tests that inspect raw payloads.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
