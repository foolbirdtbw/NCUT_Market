using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Ai;

/// <summary>
/// The shared daily allowance of model calls, for every feature that spends the deployment's key.
/// </summary>
/// <remarks>
/// <para>
/// In-process and deliberately so: this deployment runs one instance, and the alternative — a table —
/// means a write on every assistant call to defend against an abuse that has not happened. The cost of
/// being wrong is that a restart resets everyone's day, which is a trade this project makes elsewhere
/// too.
/// </para>
/// <para>
/// <b>One counter for all of it.</b> The publish-page assistant and the free-form chat draw on the
/// same number, because they are the same bill. Two counters would mean twice the configured
/// <see cref="AiOptions.DailyQuota"/> and a message that says "明天再来" to somebody who still has
/// calls left in the other feature.
/// </para>
/// <para>
/// The key carries the Beijing date and expires at the end of it, from <see cref="AppDbContext.AuditNow"/>
/// — the project's one clock. Using UTC would roll everyone's allowance over at 8am local.
/// </para>
/// </remarks>
internal sealed class AiQuota(IMemoryCache cache, IOptions<AiOptions> options)
{
    private readonly AiOptions _settings = options.Value;

    /// <summary>
    /// Charges one call, and reports whether it was allowed.
    /// </summary>
    /// <remarks>
    /// Over-spending is recorded rather than refused at the increment, so that a burst of concurrent
    /// requests cannot all read the same under-the-limit value and each conclude they were first.
    /// The caller that came back with false was over the line and is expected to refund itself if it
    /// never actually made the call.
    /// </remarks>
    public bool TrySpend(long userId, out int remaining)
    {
        var now = AppDbContext.AuditNow;

        var counter = cache.GetOrCreate(
            QuotaKey(now, userId),
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = now.Date.AddDays(1) - now;
                return new Counter();
            })!;

        var spent = Interlocked.Increment(ref counter.Value);

        remaining = Math.Max(0, _settings.DailyQuota - spent);

        return spent <= _settings.DailyQuota;
    }

    /// <summary>
    /// Hands an attempt back after a call that produced nothing.
    /// </summary>
    /// <remarks>
    /// Charging somebody for a provider outage would empty everyone's allowance the first time the
    /// vendor had a bad afternoon.
    /// </remarks>
    public void Refund(long userId)
    {
        if (cache.TryGetValue(QuotaKey(AppDbContext.AuditNow, userId), out Counter? counter)
            && counter is not null)
        {
            Interlocked.Decrement(ref counter.Value);
        }
    }

    /// <summary>
    /// How many calls are left today, without charging one.
    /// </summary>
    /// <remarks>
    /// Reads with <c>TryGetValue</c> rather than <c>GetOrCreate</c>: this is called every time the
    /// panel opens, and allocating a counter for a reader who never asks anything would fill the cache
    /// with them.
    /// </remarks>
    public int Remaining(long userId)
    {
        if (cache.TryGetValue(QuotaKey(AppDbContext.AuditNow, userId), out Counter? counter)
            && counter is not null)
        {
            return Math.Max(0, _settings.DailyQuota - Volatile.Read(ref counter.Value));
        }

        return _settings.DailyQuota;
    }

    private static string QuotaKey(DateTime now, long userId) => $"ai-quota:{now:yyyyMMdd}:{userId}";

    private sealed class Counter
    {
        public int Value;
    }
}
