using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using qBitBotNew.Config;
using qBitBotNew.Models;

namespace qBitBotNew.Services;

public sealed class MessageAggregatorService(
    GeminiService geminiService,
    RestClient restClient,
    IOptions<BotConfig> config,
    ILogger<MessageAggregatorService> logger) : IDisposable
{
    private const string Footer = "\n-# This is an automatically generated response. It may not be accurate.";

    // Key: (guildId, channelId, userId)
    private readonly ConcurrentDictionary<(ulong, ulong, ulong), PendingQuestion> _pending = new();

    public void AddMessage(ulong guildId, ulong channelId, ulong userId, ulong messageId, string content,
        IEnumerable<AttachmentInfo> attachments)
    {
        var key = (guildId, channelId, userId);
        var windowMs = config.Value.MessageAggregationWindowSeconds * 1000;

        var pending = _pending.GetOrAdd(key, _ =>
        {
            logger.LogInformation("Starting aggregation window for user {UserId} in channel {ChannelId}", userId, channelId);
            return new PendingQuestion
            {
                UserId = userId,
                ChannelId = channelId,
                GuildId = guildId,
                FirstMessageId = messageId
            };
        });

        if (!string.IsNullOrWhiteSpace(content))
            pending.MessageContents.Add(content);

        pending.Attachments.AddRange(attachments);

        // Reset or start the timer — fires after the aggregation window
        pending.AggregationTimer?.Dispose();
        pending.AggregationTimer = new Timer(OnTimerFired, key, windowMs, Timeout.Infinite);
    }

    public void MarkIntervened(ulong guildId, ulong channelId, ulong questionUserId)
    {
        var key = (guildId, channelId, questionUserId);
        if (_pending.TryGetValue(key, out var pending))
        {
            pending.Intervened = true;
            logger.LogInformation("Intervention detected for user {UserId} in channel {ChannelId}", questionUserId, channelId);
        }
    }

    public bool HasPendingQuestion(ulong guildId, ulong channelId, out ulong questionUserId)
    {
        foreach (var kvp in _pending)
        {
            if (kvp.Key.Item1 == guildId && kvp.Key.Item2 == channelId)
            {
                questionUserId = kvp.Key.Item3;
                return true;
            }
        }

        questionUserId = 0;
        return false;
    }

    private async void OnTimerFired(object? state)
    {
        var key = ((ulong, ulong, ulong))state!;

        if (!_pending.TryRemove(key, out var pending))
            return;

        if (pending.AggregationTimer != null) await pending.AggregationTimer.DisposeAsync();

        if (pending.Intervened)
        {
            logger.LogInformation("Skipping response for user {UserId} — another user intervened", pending.UserId);
            return;
        }

        var combinedQuestion = string.Join("\n", pending.MessageContents);

        if (string.IsNullOrWhiteSpace(combinedQuestion) && pending.Attachments.Count == 0)
            return;

        try
        {
            var result = await geminiService.AskAsync(combinedQuestion, pending.Attachments);

            if (result is null)
                return;

            if (!result.ShouldRespond)
            {
                logger.LogInformation("Filtered out message from user {UserId}: intent={Intent}",
                    pending.UserId, result.Intent);
                return;
            }

            var messages = FormatMessages(result);
            for (var i = 0; i < messages.Count; i++)
            {
                var props = new MessageProperties { Content = messages[i] };
                if (i == 0)
                    props.MessageReference = MessageReferenceProperties.Reply(pending.FirstMessageId);
                await restClient.SendMessageAsync(pending.ChannelId, props);
            }

            logger.LogInformation("Responded to user {UserId} in channel {ChannelId} (confidence: {Confidence})",
                pending.UserId, pending.ChannelId, result.Confidence);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process aggregated question for user {UserId}", pending.UserId);
            try
            {
                var topFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
                var errorInfo = $"`{ex.GetType().Name}: {topFrame}`";

                await restClient.SendMessageAsync(pending.ChannelId, new MessageProperties
                {
                    Content = $"Something went wrong while processing your request (auto-response). Please ping @ayymoss if this keeps happening.\n-# {errorInfo}",
                    MessageReference = MessageReferenceProperties.Reply(pending.FirstMessageId)
                });
            }
            catch (Exception replyEx)
            {
                logger.LogError(replyEx, "Failed to send error reply");
            }
        }
    }

    private static List<string> FormatMessages(GeminiResponse result)
    {
        // Fix double-escaped newlines from Gemini (literal \\n instead of \n)
        var text = result.Response.Replace("\\n", "\n");

        if (result.Confidence is "low")
        {
            text = "I'm not entirely sure about this, but here are some resources that might help:";
        }

        if (result.Resources is { Count: > 0 })
        {
            text += "\n\n**Resources:**\n" + string.Join("\n", result.Resources.Select(r => $"- <{r}>"));
        }

        return SplitForDiscord(text, Footer);
    }

    private static List<string> SplitForDiscord(string text, string footer)
    {
        const int maxLength = 2000;

        if (text.Length + footer.Length <= maxLength)
            return [text + footer];

        var messages = new List<string>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            if (remaining.Length + footer.Length <= maxLength)
            {
                messages.Add(remaining + footer);
                break;
            }

            // Find a good split point (last newline before the limit)
            var searchFrom = Math.Min(remaining.Length - 1, maxLength - 1);
            var splitAt = remaining.LastIndexOf('\n', searchFrom);
            if (splitAt <= 0)
                splitAt = maxLength;

            messages.Add(remaining[..splitAt]);
            remaining = remaining[splitAt..].TrimStart('\n');
        }

        return messages;
    }

    public void Dispose()
    {
        foreach (var kvp in _pending)
        {
            kvp.Value.AggregationTimer?.Dispose();
        }

        _pending.Clear();
    }
}
