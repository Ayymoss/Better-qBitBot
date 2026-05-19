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

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recentTimestamps = await db.Feedback
            .Where(f => f.UserId == id && f.CreatedAt >= cutoff)
            .OrderBy(f => f.CreatedAt)
            .Select(f => f.CreatedAt)
            .ToListAsync(ct);

        var used = recentTimestamps.Count;
        if (used < limit)
            return new BudgetCheck(true, used, limit, null);

        // Sliding window: next slot opens when the oldest counted call ages out past 24h.
        return new BudgetCheck(false, used, limit, recentTimestamps[0].AddHours(24));
    }
}
