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
    private static readonly EmbedFooterProperties EmbedFooter = new() { Text = "This is an automatically generated response. It may not be accurate." };

    private static readonly ActionRowProperties FeedbackButtons = new([
        new ButtonProperties("feedback_helpful", "Helpful", ButtonStyle.Success),
        new ButtonProperties("feedback_not_helpful", "Not Helpful", ButtonStyle.Danger)
    ]);

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
            using var typing = restClient.EnterTypingScope(pending.ChannelId);
            var result = await geminiService.AskAsync(combinedQuestion, pending.Attachments);

            if (result is null)
                return;

            if (!result.ShouldRespond)
            {
                logger.LogInformation("Filtered out message from user {UserId}: intent={Intent}",
                    pending.UserId, result.Intent);
                return;
            }

            var responseMessages = FormatEmbedResponse(result);
            for (var i = 0; i < responseMessages.Count; i++)
            {
                if (i == 0)
                    responseMessages[i].MessageReference = MessageReferenceProperties.Reply(pending.FirstMessageId);
                await restClient.SendMessageAsync(pending.ChannelId, responseMessages[i]);
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

    private static List<MessageProperties> FormatEmbedResponse(GeminiResponse result)
    {
        var text = result.Response.Replace("\\n", "\n");

        if (result.Confidence is "low")
            text = "I'm not entirely sure about this, but here are some resources that might help:";

        if (result.Resources is { Count: > 0 })
            text += "\n\n**Resources:**\n" + string.Join("\n", result.Resources.Select(r => $"- <{r}>"));

        var color = result.Confidence switch
        {
            "high" => new Color(67, 160, 71),
            "medium" => new Color(251, 192, 45),
            _ => new Color(255, 152, 0)
        };

        const int maxDescription = 4096;

        if (text.Length <= maxDescription)
        {
            return [new MessageProperties
            {
                Embeds = [new EmbedProperties { Description = text, Color = color, Footer = EmbedFooter }],
                Components = [FeedbackButtons]
            }];
        }

        var messages = new List<MessageProperties>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            var isLast = remaining.Length <= maxDescription;
            string chunk;

            if (isLast)
            {
                chunk = remaining;
                remaining = "";
            }
            else
            {
                var splitAt = remaining.LastIndexOf('\n', maxDescription - 1);
                if (splitAt <= 0) splitAt = maxDescription;
                chunk = remaining[..splitAt];
                remaining = remaining[splitAt..].TrimStart('\n');
            }

            var embed = new EmbedProperties { Description = chunk, Color = color };
            var props = new MessageProperties { Embeds = [embed] };

            if (isLast || remaining.Length == 0)
            {
                embed.Footer = EmbedFooter;
                props.Components = [FeedbackButtons];
            }

            messages.Add(props);
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
