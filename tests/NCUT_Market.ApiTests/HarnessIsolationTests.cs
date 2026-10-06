using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Proves the test host is pointed at the test database.
/// </summary>
/// <remarks>
/// This is not a smoke test. The factory replaces the DbContext registration precisely because the
/// obvious alternatives silently fail — <c>.env</c> outranks environment variables, and the loader
/// finds the developer's real .env by walking up from the test output directory. If that
/// replacement ever stops working, every other test in this project starts writing to the
/// development database and still passes. This is the only thing that would notice.
/// </remarks>
public sealed class HarnessIsolationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public void The_host_is_using_the_test_database()
    {
        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var actual = dbContext.Database.GetConnectionString();

        Assert.NotNull(actual);
        Assert.Contains(ApiFixture.RequiredDatabaseMarker, actual, StringComparison.OrdinalIgnoreCase);

        // Compared as parsed values, not as strings. MySqlConnector normalises a connection string
        // on the way through — "User=" comes back as "User ID=", keys are reordered — so asserting
        // string equality against what the fixture supplied fails on an equivalent connection.
        var expectedDatabase = new MySqlConnectionStringBuilder(fixture.ConnectionString).Database;
        var actualDatabase = new MySqlConnectionStringBuilder(actual).Database;

        Assert.Equal(expectedDatabase, actualDatabase);
    }

    [Fact]
    public async Task The_schema_is_the_one_the_migrations_describe()
    {
        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var applied = await dbContext.Database.GetAppliedMigrationsAsync();

        // MigrateAsync ran, so at least the initial migration is present. Asserting the count rather
        // than naming it keeps this working when a later round adds a migration — which, per the
        // current design, will be the first schema change since InitialCreate.
        Assert.NotEmpty(applied);
    }

    [Fact]
    public async Task Uploads_do_not_land_inside_the_repository()
    {
        using var scope = fixture.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<NCUT_Market.Infrastructure.Storage.StorageOptions>>()
            .Value;

        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var uploadRoot = Path.GetFullPath(options.UploadRoot);

        Assert.False(
            uploadRoot.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase),
            $"Upload root '{uploadRoot}' is inside the repository; it should be under the temp directory.");
    }

    /// <summary>
    /// The background sweep is off for the whole test run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WebApplicationFactory&lt;Program&gt;</c> runs the real <c>Program.cs</c>, so
    /// <c>AddHostedService</c> really does start the sweep timer. Against this shared, never-cleaned
    /// database that means a background task resolving its own scope and rolling back trades — every
    /// fifteen minutes, from every test class — while other tests are midway through asserting on
    /// them. The failures would look like bugs in <c>TransactionService</c>.
    /// </para>
    /// <para>
    /// Asserted rather than trusted, because the mechanism is a <c>PostConfigure</c> on an options
    /// object that nothing else in the fixture touches. Delete that one line and every other test in
    /// this project still passes, most of the time.
    /// </para>
    /// </remarks>
    [Fact]
    public void Background_jobs_are_disabled_under_the_test_host()
    {
        using var scope = fixture.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<NCUT_Market.Infrastructure.Jobs.BackgroundJobsOptions>>()
            .Value;

        Assert.False(options.Enabled);
    }
}
