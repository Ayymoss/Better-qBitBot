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
                BotMessageId = botMessageId.ToDbId(),
                ChannelId = channelId.ToDbId(),
                UserId = userId.ToDbId(),
                GuildId = guildId.ToDbId(),
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

        // SQLite EF can't translate DateTimeOffset comparisons; pull the columns we need
        // and aggregate in memory. At hundreds-of-rows scale this is trivial.
        var rows = await db.Feedback
            .Select(f => new
            {
                f.CreatedAt,
                f.Helpful,
                f.Intent,
                f.Confidence,
                f.PromptTokens,
                f.CachedTokens,
                f.OutputTokens,
                f.ThoughtTokens,
                f.Prompt
            })
            .ToListAsync(ct);

        var responsesAll = rows.Count;
        var responsesLast24h = rows.Count(r => r.CreatedAt >= cutoff24h);
        var responsesLast7d = rows.Count(r => r.CreatedAt >= cutoff7d);

        var ratedCount = rows.Count(r => r.Helpful != null);
        var helpfulCount = rows.Count(r => r.Helpful == true);

        var window7d = rows.Where(r => r.CreatedAt >= cutoff7d).ToList();
        var window7dOnTopic = window7d.Where(r => r.Intent == "on_topic").ToList();

        var onTopicCount = window7dOnTopic.Count;
        var piracyCount = window7d.Count(r => r.Intent == "piracy");
        var offTopicCount = window7d.Count(r => r.Intent == "off_topic");

        // Confidence + low-prompt stats only make sense for on-topic rows; rejections are
        // always low-confidence by their nature and would skew the breakdown.
        var highCount = window7dOnTopic.Count(r => r.Confidence == ConfidenceLevel.High);
        var mediumCount = window7dOnTopic.Count(r => r.Confidence == ConfidenceLevel.Medium);
        var lowCount = window7dOnTopic.Count(r => r.Confidence == ConfidenceLevel.Low);

        var promptTokens = window7d.Sum(r => (long)r.PromptTokens);
        var cachedTokens = window7d.Sum(r => (long)r.CachedTokens);
        var outputTokens = window7d.Sum(r => (long)r.OutputTokens);
        var thoughtTokens = window7d.Sum(r => (long)r.ThoughtTokens);

        var lowPrompts = window7dOnTopic
            .Where(r => r.Confidence == ConfidenceLevel.Low)
            .OrderByDescending(r => r.CreatedAt)
            .Take(5)
            .Select(r => r.Prompt)
            .ToList();

        return new StatsSnapshot(
            responsesLast24h,
            responsesLast7d,
            responsesAll,
            ratedCount,
            helpfulCount,
            onTopicCount,
            piracyCount,
            offTopicCount,
            highCount,
            mediumCount,
            lowCount,
            promptTokens,
            cachedTokens,
            outputTokens,
            thoughtTokens,
            lowPrompts);
    }

    // Used by the in-thread auto-reply path: if the bot has previously responded inside this
    // channel id (which for threads is the thread's own id), treat the thread as a
    // bot-managed conversation and reply without requiring @mention.
    public async Task<bool> HasBotRespondedInChannelAsync(ulong channelId, CancellationToken ct = default)
    {
        var id = channelId.ToDbId();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Feedback.AnyAsync(f => f.ChannelId == id, ct);
    }

    public async Task<string?> GetThoughtSummaryAsync(ulong botMessageId, CancellationToken ct = default)
    {
        try
        {
            var id = botMessageId.ToDbId();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Feedback
                .Where(f => f.BotMessageId == id)
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
            var id = botMessageId.ToDbId();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entry = await db.Feedback.SingleOrDefaultAsync(f => f.BotMessageId == id, ct);
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
