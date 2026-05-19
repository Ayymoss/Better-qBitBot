using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using qBitBotNew.Models;
using qBitBotNew.Persistence;
using qBitBotNew.Persistence.Entities;

namespace qBitBotNew.Services;

public sealed partial class FeedbackService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<FeedbackService> logger)
{
    public async Task RecordResponseAsync(
        GeminiResponse response,
        string prompt,
        ulong botMessageId,
        ulong channelId,
        ulong userId,
        ulong? guildId,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Feedback.Add(new FeedbackEntry
            {
                BotMessageId = botMessageId,
                ChannelId = channelId,
                UserId = userId,
                GuildId = guildId,
                Prompt = prompt,
                Response = response.Response,
                Intent = response.Intent,
                Confidence = response.Confidence,
                ThoughtSummary = response.ThoughtSummary,
                PromptTokens = response.Usage.Prompt,
                CachedTokens = response.Usage.Cached,
                OutputTokens = response.Usage.Output,
                ThoughtTokens = response.Usage.Thoughts,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Persistence failures must not break the bot's ability to reply — log and move on.
            LogPersistResponseFailed(ex, botMessageId);
        }
    }

    public async Task<StatsSnapshot> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var cutoff24h = now - TimeSpan.FromHours(24);
        var cutoff7d = now - TimeSpan.FromDays(7);

        var responsesLast24h = await db.Feedback.CountAsync(f => f.CreatedAt >= cutoff24h, ct);
        var responsesLast7d = await db.Feedback.CountAsync(f => f.CreatedAt >= cutoff7d, ct);
        var responsesAll = await db.Feedback.CountAsync(ct);

        var ratedCount = await db.Feedback.CountAsync(f => f.Helpful != null, ct);
        var helpfulCount = await db.Feedback.CountAsync(f => f.Helpful == true, ct);

        var window7d = db.Feedback.Where(f => f.CreatedAt >= cutoff7d);
        var highCount = await window7d.CountAsync(f => f.Confidence == ConfidenceLevel.High, ct);
        var mediumCount = await window7d.CountAsync(f => f.Confidence == ConfidenceLevel.Medium, ct);
        var lowCount = await window7d.CountAsync(f => f.Confidence == ConfidenceLevel.Low, ct);

        var tokenTotals = await window7d
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Prompt = g.Sum(f => (long)f.PromptTokens),
                Cached = g.Sum(f => (long)f.CachedTokens),
                Output = g.Sum(f => (long)f.OutputTokens),
                Thoughts = g.Sum(f => (long)f.ThoughtTokens)
            })
            .FirstOrDefaultAsync(ct);

        var lowPrompts = await window7d
            .Where(f => f.Confidence == ConfidenceLevel.Low)
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .Select(f => f.Prompt)
            .ToListAsync(ct);

        return new StatsSnapshot(
            responsesLast24h,
            responsesLast7d,
            responsesAll,
            ratedCount,
            helpfulCount,
            highCount,
            mediumCount,
            lowCount,
            tokenTotals?.Prompt ?? 0,
            tokenTotals?.Cached ?? 0,
            tokenTotals?.Output ?? 0,
            tokenTotals?.Thoughts ?? 0,
            lowPrompts);
    }

    public async Task<string?> GetThoughtSummaryAsync(ulong botMessageId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Feedback
                .Where(f => f.BotMessageId == botMessageId)
                .Select(f => f.ThoughtSummary)
                .SingleOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            LogPersistResponseFailed(ex, botMessageId);
            return null;
        }
    }

    public async Task RecordRatingAsync(ulong botMessageId, ulong userId, ulong channelId, bool helpful, string? reason = null, CancellationToken ct = default)
    {
        LogFeedback(botMessageId, userId, channelId, helpful);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entry = await db.Feedback.SingleOrDefaultAsync(f => f.BotMessageId == botMessageId, ct);
            if (entry is null)
            {
                LogRatingForUnknownMessage(botMessageId);
                return;
            }
            entry.Helpful = helpful;
            entry.Reason = reason;
            entry.RatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LogPersistRatingFailed(ex, botMessageId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ResponseFeedback — MessageId: {MessageId}, UserId: {UserId}, ChannelId: {ChannelId}, Helpful: {Helpful}")]
    public partial void LogFeedback(ulong messageId, ulong userId, ulong channelId, bool helpful);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist response for message {MessageId}")]
    private partial void LogPersistResponseFailed(Exception ex, ulong messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist rating for message {MessageId}")]
    private partial void LogPersistRatingFailed(Exception ex, ulong messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rating clicked for message {MessageId} but no feedback row found")]
    private partial void LogRatingForUnknownMessage(ulong messageId);
}
