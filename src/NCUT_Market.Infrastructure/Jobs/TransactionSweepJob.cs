using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Jobs;

/// <summary>
/// Applies the trade flow's deadlines: expired proposals, and trades that have run out their day.
/// </summary>
/// <remarks>
/// <para>
/// The first <see cref="BackgroundService"/> in this project. Everything about the shape here is the
/// ordinary one: a <see cref="PeriodicTimer"/>, a fresh scope per tick because the services are
/// scoped, and a per-tick catch so one bad round does not take the job down for the life of the host.
/// </para>
/// <para>
/// The logic itself lives in <see cref="ITransactionService.SweepAsync"/>, not here. That split is
/// what makes the deadlines testable: a test calls the sweep with a time of its own choosing instead
/// of waiting a day or driving this loop.
/// </para>
/// </remarks>
internal sealed class TransactionSweepJob(
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundJobsOptions> options,
    ILogger<TransactionSweepJob> logger) : BackgroundService
{
    /// <summary>How often the deadlines are checked.</summary>
    /// <remarks>
    /// Both deadlines are a day, so a quarter of an hour is plenty — the worst a late tick can do is
    /// leave a listing in "交易中" fifteen minutes longer than it strictly had to be.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("交易超时扫描：这个环境关掉了后台任务，不启动。");

            return;
        }

        logger.LogInformation("交易超时扫描：每 {Minutes} 分钟一轮。", Interval.TotalMinutes);

        // PeriodicTimer waits a full interval before its first tick, which is what we want: there is
        // nothing to do at startup that will not still be there in fifteen minutes, and sweeping
        // immediately would put a database pass in the middle of the host's own startup.
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure, and not worth a log line of its own.
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            var transactions = scope.ServiceProvider.GetRequiredService<ITransactionService>();

            // AuditNow, never DateTime.UtcNow. Every timestamp column in this database holds Beijing
            // wall-clock time, so comparing against UTC would put every deadline eight hours out —
            // and the sweep would quietly settle trades a third of a day early.
            var changed = await transactions.SweepAsync(AppDbContext.AuditNow, stoppingToken);

            if (changed > 0)
            {
                logger.LogInformation("交易超时扫描：处理了 {Count} 条。", changed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping mid-sweep. The next round picks up whatever was left.
        }
        catch (Exception exception)
        {
            // Swallowed on purpose. A transient database failure, or a row in a state the sweep did
            // not anticipate, must not end the job — it would never run again, and nothing else in
            // the application enforces these deadlines.
            logger.LogError(exception, "交易超时扫描这一轮失败了，下一轮会重试。");
        }
    }
}
