using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class MessageCreateHandler(
    GeminiService geminiService,
    RateLimiterService rateLimiterService,
    RestClient restClient,
    GatewayClient gatewayClient,
    ILogger<MessageCreateHandler> logger) : IMessageCreateGatewayHandler
{
    private static readonly EmbedFooterProperties EmbedFooter = new() { Text = "This is a generated response. It may not be accurate." };

    private static readonly ActionRowProperties FeedbackButtons = new([
        new ButtonProperties("feedback_helpful", "Helpful", ButtonStyle.Success),
        new ButtonProperties("feedback_not_helpful", "Not Helpful", ButtonStyle.Danger)
    ]);
    public async ValueTask HandleAsync(Message message)
    {
        // Ignore bots and DMs
        if (message.Author.IsBot)
            return;

        if (message.GuildId is not { } guildId)
            return;

        var botUserId = gatewayClient.Id;

        // Check if this is a reply to the bot's message
        if (message.ReferencedMessage is { } referenced)
        {
            // Someone replying to the bot — continuation or invocation on behalf
            if (referenced.Author.Id == botUserId)
            {
                await HandleReplyToBot(message, referenced);
                return;
            }

            // Someone @mentioning the bot while replying to another user — invocation on behalf
            if (IsBotMentioned(message, botUserId))
            {
                await HandleInvocationOnBehalf(message, referenced);
                return;
            }
        }

        // Check if this is a direct @mention of the bot
        if (IsBotMentioned(message, botUserId))
        {
            await HandleDirectMention(message);
            return;
        }

        // No auto-response — bot only responds when explicitly invoked
    }

    private async Task HandleReplyToBot(Message message, RestMessage botMessage)
    {
        if (rateLimiterService.IsRateLimited(message.Author.Id, out var remaining))
        {
            await NotifyCooldown(message, remaining);
            return;
        }

        // Walk the reply chain to build multi-turn conversation history
        var chain = new List<(bool IsBot, string Content)>();
        var current = botMessage as RestMessage;
        while (current is not null)
        {
            var isBot = current.Author.Id == gatewayClient.Id;
            // Bot messages use embeds; user messages use Content
            var content = isBot
                ? current.Embeds.FirstOrDefault()?.Description ?? current.Content
                : current.Content;
            if (!string.IsNullOrEmpty(content))
                chain.Add((isBot, content));
            current = current.ReferencedMessage;
        }

        chain.Reverse();

        // Build proper multi-turn conversation for Gemini
        var conversation = new List<GeminiMessage>();
        foreach (var (isBot, content) in chain)
            conversation.Add(new GeminiMessage(isBot ? "model" : "user", content));

        // Add the current follow-up as the final user turn
        conversation.Add(new GeminiMessage("user", message.Content));

        var attachments = ExtractAttachments(message);
        await RespondWithConversation(message, conversation, attachments);
    }

    private async Task HandleInvocationOnBehalf(Message message, RestMessage targetMessage)
    {
        if (rateLimiterService.IsRateLimited(message.Author.Id, out var remaining))
        {
            await NotifyCooldown(message, remaining);
            return;
        }

        // Gather context from the target user's messages, always including the replied-to message
        var (conversation, attachments) = await GatherUserContext(message, targetMessage.Author.Id, targetMessage);

        await RespondWithConversation(message, conversation, attachments);
    }

    private async Task HandleDirectMention(Message message)
    {
        if (rateLimiterService.IsRateLimited(message.Author.Id, out var remaining))
        {
            await NotifyCooldown(message, remaining);
            return;
        }

        // Gather context from the invoking user's messages only
        var (conversation, attachments) = await GatherUserContext(message, message.Author.Id);

        await RespondWithConversation(message, conversation, attachments);
    }

    private async Task<(List<GeminiMessage> Conversation, List<AttachmentInfo> Attachments)> GatherUserContext(
        Message invokingMessage, ulong contextUserId, RestMessage? anchorMessage = null)
    {
        var botUserId = gatewayClient.Id;

        // Fetch recent channel messages
        var recentMessages = await restClient.GetMessagesAroundAsync(invokingMessage.ChannelId, invokingMessage.Id, 50);
        var now = DateTimeOffset.UtcNow;

        // IDs to exclude from the time-filtered search (handled separately)
        var excludeIds = new HashSet<ulong> { invokingMessage.Id };
        if (anchorMessage is not null)
            excludeIds.Add(anchorMessage.Id);

        // Include all messages within 12 hours for conversation context (including bot messages)
        var channelMessages = recentMessages
            .Where(m => !excludeIds.Contains(m.Id))
            .Where(m => now - m.CreatedAt < TimeSpan.FromHours(12))
            .OrderBy(m => m.Id)
            .ToList();

        // Collect attachments from the invoking message, anchor, and context user's messages
        var attachments = ExtractAttachments(invokingMessage);
        if (anchorMessage is not null)
            attachments.AddRange(ExtractAttachments(anchorMessage));
        foreach (var m in channelMessages.Where(m => m.Author.Id == contextUserId))
            attachments.AddRange(ExtractAttachments(m));

        // Build background context string
        var contextParts = new List<string>();
        var recentThreshold = TimeSpan.FromHours(2);

        // Always include the anchor message first if present (the replied-to message, regardless of age)
        if (anchorMessage is not null)
        {
            var name = GetDisplayName(anchorMessage.Author);
            var time = anchorMessage.CreatedAt.ToString("HH:mm");
            contextParts.Add($"[Primary question — this is the message the bot was invoked on]:\n[{time}] {name}: {anchorMessage.Content}{(anchorMessage.Attachments.Any() ? " [has attached image]" : "")}");
        }

        var olderMessages = channelMessages.Where(m => now - m.CreatedAt >= recentThreshold).ToList();
        var recentContextMessages = channelMessages.Where(m => now - m.CreatedAt < recentThreshold).ToList();

        if (olderMessages.Count > 0)
        {
            contextParts.Add("[Older context — for background only]");
            foreach (var m in olderMessages)
                contextParts.Add(FormatContextMessage(m, botUserId));
        }

        if (recentContextMessages.Count > 0)
        {
            contextParts.Add("[Relevant context — within the last 2 hours]");
            foreach (var m in recentContextMessages)
                contextParts.Add(FormatContextMessage(m, botUserId));
        }

        // Build the conversation as: background context (user) → ack (model) → current question (user)
        var conversation = new List<GeminiMessage>();

        if (contextParts.Count > 0)
        {
            conversation.Add(new GeminiMessage("user", string.Join("\n", contextParts)));
            conversation.Add(new GeminiMessage("model", "Understood. I've read the conversation context. What's the question?"));
        }

        // Handle the invoking message itself
        var botMention = $"<@{botUserId}>";
        var invokerText = invokingMessage.Content.Replace(botMention, "").Trim();

        if (!string.IsNullOrWhiteSpace(invokerText))
        {
            var invokerName = GetDisplayName(invokingMessage.Author);
            var invokerTime = invokingMessage.CreatedAt.ToString("HH:mm");
            conversation.Add(new GeminiMessage("user", $"[{invokerTime}] {invokerName}: {invokerText}"));
        }
        else if (anchorMessage is not null)
        {
            conversation.Add(new GeminiMessage("user", "Answer the primary question from the context above."));
        }
        else
        {
            conversation.Add(new GeminiMessage("user", "Answer based on the user's recent messages and any attached images."));
        }

        return (conversation, attachments);
    }

    private string FormatContextMessage(RestMessage m, ulong botUserId)
    {
        var time = m.CreatedAt.ToString("HH:mm");
        if (m.Author.Id == botUserId)
        {
            // Bot messages store their response in embed descriptions
            var content = m.Embeds.FirstOrDefault()?.Description ?? m.Content;
            return $"[{time}] qBitBot (you): {content}";
        }
        var name = GetDisplayName(m.Author);
        return $"[{time}] {name}: {m.Content}{(m.Attachments.Any() ? " [has attached image]" : "")}";
    }

    private static string GetDisplayName(User author) =>
        (author as GuildUser)?.Nickname ?? author.GlobalName ?? author.Username;

    private async Task RespondWithConversation(Message message, List<GeminiMessage> conversation, List<AttachmentInfo> attachments, bool isDirectInvocation = true)
    {
        try
        {
            using var typing = restClient.EnterTypingScope(message.ChannelId);
            var result = await geminiService.AskAsync(conversation, attachments);

            if (result is null)
                return;

            // For direct invocations (@mention / reply-to), give feedback on why we can't help
            if (!result.ShouldRespond)
            {
                if (isDirectInvocation)
                {
                    var rejection = result.IsPiracy
                        ? "Sorry, I can't help with that. I'm only able to assist with qBitTorrent client questions — topics related to piracy or illegal downloads are outside my scope."
                        : "That doesn't seem to be a qBitTorrent question. I can help with qBitTorrent client configuration, troubleshooting, and usage — feel free to ask!";

                    await restClient.SendMessageAsync(message.ChannelId, new MessageProperties
                    {
                        Embeds = [new EmbedProperties
                        {
                            Description = rejection,
                            Color = new Color(158, 158, 158), // grey
                            Footer = EmbedFooter
                        }],
                        MessageReference = MessageReferenceProperties.Reply(message.Id)
                    });
                }
                return;
            }

            var responseMessages = FormatEmbedResponse(result);
            for (var i = 0; i < responseMessages.Count; i++)
            {
                if (i == 0)
                    responseMessages[i].MessageReference = MessageReferenceProperties.Reply(message.Id);
                await restClient.SendMessageAsync(message.ChannelId, responseMessages[i]);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to respond to message {MessageId} from user {UserId}", message.Id, message.Author.Id);
            await SendErrorReply(message.ChannelId, message.Id, "direct invocation", ex);
        }
    }

    private static bool IsBotMentioned(Message message, ulong botUserId) =>
        message.MentionedUsers.Any(u => u.Id == botUserId);

    private static List<AttachmentInfo> ExtractAttachments(RestMessage message) =>
        message.Attachments
            .Where(a => a.ContentType is not null)
            .Select(a => new AttachmentInfo(a.Url, a.ContentType!))
            .ToList();

    private async Task NotifyCooldown(Message message, TimeSpan remaining)
    {
        try
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);

            // Add hourglass reaction and send a visible cooldown notice
            await restClient.AddMessageReactionAsync(message.ChannelId, message.Id, new ReactionEmojiProperties("\u23f3"));

            var notice = await restClient.SendMessageAsync(message.ChannelId, new MessageProperties
            {
                Content = $"You're on cooldown — try again in **{seconds}s**.",
                MessageReference = MessageReferenceProperties.Reply(message.Id)
            });

            // Delete the notice after 5s, then remove the reaction once the cooldown expires
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    await restClient.DeleteMessageAsync(message.ChannelId, notice.Id);

                    var reactionDelay = remaining - TimeSpan.FromSeconds(5);
                    if (reactionDelay > TimeSpan.Zero)
                        await Task.Delay(reactionDelay);

                    await restClient.DeleteCurrentUserMessageReactionAsync(message.ChannelId, message.Id, new ReactionEmojiProperties("\u23f3"));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up cooldown notification");
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send cooldown notification");
        }
    }

    private async Task SendErrorReply(ulong channelId, ulong replyToMessageId, string context, Exception ex)
    {
        try
        {
            var topFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
            var errorInfo = $"`{ex.GetType().Name}: {topFrame}`";

            await restClient.SendMessageAsync(channelId, new MessageProperties
            {
                Content = $"Something went wrong while processing your request ({context}). Please ping @ayymoss if this keeps happening.\n-# {errorInfo}",
                MessageReference = MessageReferenceProperties.Reply(replyToMessageId)
            });
        }
        catch (Exception replyEx)
        {
            logger.LogError(replyEx, "Failed to send error reply");
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

        // Split into multiple embeds for very long responses
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
}
