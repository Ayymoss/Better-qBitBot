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
