namespace NCUT_Market.Infrastructure.Jobs;

/// <summary>
/// Whether the recurring background work runs in this host.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the API test suite boots the real <c>Program.cs</c> through
/// <c>WebApplicationFactory</c>. A registered <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// really does start there, and the trade sweep would then run its timer against the shared, never
/// cleaned test database — from every test class, once per fixture. Tests would go from deterministic
/// to occasionally-red for reasons no assertion mentions, and the cause would be a timer.
/// </para>
/// <para>
/// The fixture turns this off with <c>PostConfigure</c>, the same mechanism already used to replace
/// the JWT key and the upload root. Default true, so a host that says nothing gets the jobs — being
/// silently off in production would be a much worse failure than being on in a test.
/// </para>
/// </remarks>
public sealed class BackgroundJobsOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>Whether to start the recurring jobs at all.</summary>
    public bool Enabled { get; set; } = true;
}
