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
    /// Reads an image row's four storage keys, in the order original/large/medium/thumbnail.
    /// </summary>
    public async Task<string[]> ImageKeysAsync(long imageId)
    {
        await using var scope = Services.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var image = await dbContext.ProductImages
            .Where(x => x.Id == imageId)
            .Select(x => new { x.OriginalKey, x.LargeKey, x.MediumKey, x.ThumbnailKey })
            .FirstAsync();

        return [image.OriginalKey, image.LargeKey, image.MediumKey, image.ThumbnailKey];
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
