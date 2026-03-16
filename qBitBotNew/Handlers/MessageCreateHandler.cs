using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using qBitBotNew.Config;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class MessageCreateHandler(
    GeminiService geminiService,
    MessageAggregatorService aggregatorService,
    RateLimiterService rateLimiterService,
    RestClient restClient,
    GatewayClient gatewayClient,
    IOptions<BotConfig> config,
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

        // Check if another user is posting in a channel with a pending question (intervention detection)
        if (aggregatorService.HasPendingQuestion(guildId, message.ChannelId, out var questionUserId)
            && message.Author.Id != questionUserId)
        {
            aggregatorService.MarkIntervened(guildId, message.ChannelId, questionUserId);
            return;
        }

        // Auto-response logic: only for new users
        await HandlePotentialNewUserQuestion(message, guildId);
    }

    private async Task HandleReplyToBot(Message message, RestMessage botMessage)
    {
        if (rateLimiterService.IsRateLimited(message.Author.Id))
        {
            await ReactWithCooldown(message);
            return;
        }

        // Walk the reply chain to build full conversation history
        var chain = new List<(string Role, string Content)>();
        var current = botMessage as RestMessage;
        while (current is not null)
        {
            var isBot = current.Author.Id == gatewayClient.Id;
            var role = isBot ? "Bot" : GetDisplayName(current.Author);
            // Bot messages use embeds; user messages use Content
            var content = !string.IsNullOrEmpty(current.Content)
                ? current.Content
                : current.Embeds.FirstOrDefault()?.Description ?? "";
            chain.Add((role, content));
            current = current.ReferencedMessage;
        }

        chain.Reverse();
        var history = string.Join("\n\n", chain.Select(c => $"[{c.Role}]\n{c.Content}"));
        var userName = GetDisplayName(message.Author);
        var context = $"{history}\n\n[{userName}'s follow-up — respond to THIS]\n{message.Content}";
        var attachments = ExtractAttachments(message);

        await RespondDirectly(message, context, attachments);
    }

    private async Task HandleInvocationOnBehalf(Message message, RestMessage targetMessage)
    {
        if (rateLimiterService.IsRateLimited(message.Author.Id))
        {
            await ReactWithCooldown(message);
            return;
        }

        // Gather context from the target user's messages, always including the replied-to message
        var (context, attachments) = await GatherUserContext(message, targetMessage.Author.Id, targetMessage);

        await RespondDirectly(message, context, attachments);
    }

    private async Task HandleDirectMention(Message message)
    {
        if (rateLimiterService.IsRateLimited(message.Author.Id))
        {
            await ReactWithCooldown(message);
            return;
        }

        // Gather context from the invoking user's messages only
        var (context, attachments) = await GatherUserContext(message, message.Author.Id);

        await RespondDirectly(message, context, attachments);
    }

    private async Task<(string Context, List<AttachmentInfo> Attachments)> GatherUserContext(
        Message invokingMessage, ulong contextUserId, RestMessage? anchorMessage = null)
    {
        // Fetch recent channel messages
        var recentMessages = await restClient.GetMessagesAroundAsync(invokingMessage.ChannelId, invokingMessage.Id, 50);
        var now = DateTimeOffset.UtcNow;

        // IDs to exclude from the time-filtered search (handled separately)
        var excludeIds = new HashSet<ulong> { invokingMessage.Id };
        if (anchorMessage is not null)
            excludeIds.Add(anchorMessage.Id);

        // Include all non-bot messages within 12 hours for conversation context
        var channelMessages = recentMessages
            .Where(m => !m.Author.IsBot)
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

        // Build context with recency labels
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
            {
                var name = GetDisplayName(m.Author);
                var time = m.CreatedAt.ToString("HH:mm");
                contextParts.Add($"[{time}] {name}: {m.Content}{(m.Attachments.Any() ? " [has attached image]" : "")}");
            }
        }

        if (recentContextMessages.Count > 0)
        {
            contextParts.Add("[Relevant context — within the last 2 hours]");
            foreach (var m in recentContextMessages)
            {
                var name = GetDisplayName(m.Author);
                var time = m.CreatedAt.ToString("HH:mm");
                contextParts.Add($"[{time}] {name}: {m.Content}{(m.Attachments.Any() ? " [has attached image]" : "")}");
            }
        }

        // Handle the invoking message itself
        var botMention = $"<@{gatewayClient.Id}>";
        var invokerText = invokingMessage.Content.Replace(botMention, "").Trim();

        if (!string.IsNullOrWhiteSpace(invokerText))
        {
            var invokerName = GetDisplayName(invokingMessage.Author);
            var invokerTime = invokingMessage.CreatedAt.ToString("HH:mm");
            contextParts.Add($"[Current question — respond to THIS]:\n[{invokerTime}] {invokerName}: {invokerText}");
        }
        else if (anchorMessage is null)
        {
            contextParts.Add("[The bot was invoked — answer based on the user's recent messages above and any attached images]");
        }

        return (string.Join("\n", contextParts), attachments);
    }

    private static string GetDisplayName(User author) =>
        (author as GuildUser)?.Nickname ?? author.GlobalName ?? author.Username;

    private async Task HandlePotentialNewUserQuestion(Message message, ulong guildId)
    {
        // Check user age — only auto-respond to new users (joined < threshold)
        try
        {
            var guildUser = await restClient.GetGuildUserAsync(guildId, message.Author.Id);
            var joinedAt = guildUser.JoinedAt;
            var threshold = TimeSpan.FromHours(config.Value.NewUserThresholdHours);

            if (DateTimeOffset.UtcNow - joinedAt > threshold)
                return; // Not a new user, ignore
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get guild user info for {UserId}", message.Author.Id);
            return;
        }

        // New user — add to aggregation window
        var attachments = ExtractAttachments(message);
        aggregatorService.AddMessage(
            guildId,
            message.ChannelId,
            message.Author.Id,
            message.Id,
            message.Content,
            attachments);
    }

    private async Task RespondDirectly(Message message, string context, List<AttachmentInfo> attachments, bool isDirectInvocation = true)
    {
        try
        {
            using var typing = restClient.EnterTypingScope(message.ChannelId);
            var result = await geminiService.AskAsync(context, attachments);

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

    private async Task ReactWithCooldown(Message message)
    {
        try
        {
            await restClient.AddMessageReactionAsync(message.ChannelId, message.Id, new ReactionEmojiProperties("\u23f3"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add cooldown reaction");
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
