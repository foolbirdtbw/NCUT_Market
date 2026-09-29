using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NCUT_Market.Infrastructure;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.IntegrationTests;

/// <summary>
/// Brings the dedicated test database up to date once for the whole collection.
/// </summary>
/// <remarks>
/// <para>
/// It builds a real <see cref="ServiceProvider"/> and calls the same
/// <see cref="DependencyInjection.AddInfrastructure"/> the API calls, so the tests exercise the
/// production container wiring rather than a hand-rolled <c>DbContextOptions</c>. That is deliberate:
/// a fixture that configures EF its own way would keep passing after the API's registration broke.
/// </para>
/// <para>
/// Tests share one database and do not clean up after themselves — row names carry a random suffix
/// instead. That is why the collection disables parallelization: two tests inserting a
/// same-named row concurrently would collide on a unique index and fail intermittently.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    /// <summary>Environment variable holding the test connection string.</summary>
    public const string ConnectionStringVariable = "NCUT_TEST_CONNECTION";

    /// <summary>
    /// Substring the connection string must contain. A guard, not a formality: these tests write rows,
    /// and silently pointing them at the development database would corrupt real data with no error.
    /// </summary>
    public const string RequiredDatabaseMarker = "ncut_market_test";

    private ServiceProvider? _serviceProvider;

    public DatabaseFixture()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Set the {ConnectionStringVariable} environment variable to the {RequiredDatabaseMarker} " +
                "connection string before running these tests.");
        }

        if (!connectionString.Contains(RequiredDatabaseMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} must point at the '{RequiredDatabaseMarker}' database. " +
                "These tests write rows and never clean up.");
        }

        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public IServiceProvider Services => _serviceProvider
        ?? throw new InvalidOperationException(
            $"{nameof(DatabaseFixture)} has not been initialized. Was it used outside a collection?");

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString
            })
            .Build();

        _serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> against a fresh scoped <see cref="AppDbContext"/>.
    /// </summary>
    /// <remarks>
    /// A new scope per call is what makes reloading from the database meaningful — a shared context
    /// would hand back the identity-mapped instance instead of reading the row back.
    /// </remarks>
    public async Task WithDbContextAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();

        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

/// <summary>
/// Declares the shared database for every test class in this assembly.
/// </summary>
/// <remarks>
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> is required, not tidiness: the
/// tests share one database, and the unique index on <c>dormitory_areas.name</c> turns a concurrent
/// insert into an intermittent failure.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
