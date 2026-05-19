using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using qBitBotNew.Config;
using qBitBotNew.Models;
using qBitBotNew.Persistence;

namespace qBitBotNew.Services;

public sealed class RateLimiterService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<BotConfig> config)
{
    public async Task<BudgetCheck> CheckBudgetAsync(ulong userId, CancellationToken ct = default)
    {
        var limit = config.Value.DailyTurnBudget;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        var id = userId.ToDbId();

        // SQLite EF provider can't translate DateTimeOffset comparisons reliably, so the
        // time-window filter runs in memory. The DB-side filter is on UserId (long), which
        // bounds the dataset to a single user's history — small enough to be fine.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var allTimestamps = await db.Feedback
            .Where(f => f.UserId == id)
            .Select(f => f.CreatedAt)
            .ToListAsync(ct);

        var recent = allTimestamps
            .Where(t => t >= cutoff)
            .OrderBy(t => t)
            .ToList();

        var used = recent.Count;
        if (used < limit)
            return new BudgetCheck(true, used, limit, null);

        // Sliding window: next slot opens when the oldest counted call ages out past 24h.
        return new BudgetCheck(false, used, limit, recent[0].AddHours(24));
    }
}
