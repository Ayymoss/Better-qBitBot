using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using qBitBotNew.Config;
using qBitBotNew.Helpers;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed partial class MessageCreateHandler(
    GeminiService geminiService,
    RateLimiterService rateLimiterService,
    FeedbackService feedbackService,
    GreetService greetService,
    RestClient restClient,
    GatewayClient gatewayClient,
    IOptions<BotConfig> botConfig,
    IHostApplicationLifetime lifetime,
    ILogger<MessageCreateHandler> logger) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        // Ignore bots and DMs
        if (message.Author.IsBot)
            return;

        if (message.GuildId is not { } guildId)
            return;

        var ct = lifetime.ApplicationStopping;
        var botUserId = gatewayClient.Id;

        // If this message is a reply to someone we're considering greeting, cancel the
        // pending greet — humans are already engaged, we don't need to butt in.
        if (message.ReferencedMessage is { } refMsg && !refMsg.Author.IsBot)
            greetService.Cancel(refMsg.Author.Id);

        // Track new users so the GreetWorker can offer the bot to them after a quiet period.
        // Their first invocation of the bot through any path (including the greet button) will
        // record a Feedback row, which suppresses future greet attempts for them.
        if (botConfig.Value.GreetEnabled
            && message.Author is GuildUser guildUser
            && guildUser.JoinedAt is { } joinedAt
            && DateTimeOffset.UtcNow - joinedAt < TimeSpan.FromHours(botConfig.Value.NewUserThresholdHours)
            && !greetService.IsAlreadyGreeted(message.Author.Id))
        {
            greetService.TrackOrUpdate(message.Author.Id, message.ChannelId, message.Id);
        }

        // Check if this is a reply to the bot's message
        if (message.ReferencedMessage is { } referenced)
        {
            // Someone replying to the bot — continuation or invocation on behalf
            if (referenced.Author.Id == botUserId)
            {
                await HandleReplyToBot(message, referenced, ct);
                return;
            }

            // Someone @mentioning the bot while replying to another user — invocation on behalf
            if (IsBotMentioned(message, botUserId))
            {
                await HandleInvocationOnBehalf(message, referenced, ct);
                return;
            }
        }

        // Check if this is a direct @mention of the bot
        if (IsBotMentioned(message, botUserId))
        {
            await HandleDirectMention(message, ct);
            return;
        }

        // Auto-reply inside a thread the bot has previously responded in. Threads spawned
        // by an earlier invocation are purpose-built for a conversation, so we treat every
        // user message there as a direct question without needing the @mention.
        if (IsThreadChannel(message)
            && await feedbackService.HasBotRespondedInChannelAsync(message.ChannelId, ct))
        {
            await HandleDirectMention(message, ct);
            return;
        }

        // No auto-response — bot only responds when explicitly invoked
    }

    private static bool IsThreadChannel(Message message) => message.Channel is GuildThread;

    private async Task HandleReplyToBot(Message message, RestMessage botMessage, CancellationToken ct = default)
    {
        var budget = await rateLimiterService.CheckBudgetAsync(message.Author.Id, ct);
        if (!budget.Allowed)
        {
            await NotifyBudgetExceeded(message, budget, ct);
            return;
        }

        // Walk the reply chain to build multi-turn conversation history
        List<(bool IsBot, string Content)> chain = [];
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
        List<GeminiMessage> conversation = [];
        foreach (var (isBot, content) in chain)
            conversation.Add(new GeminiMessage(isBot ? "model" : "user", content));

        // Add the current follow-up as the final user turn. Strip the bot's @mention token
        // (Discord auto-pings on reply by default) so Gemini doesn't see raw <@id> noise.
        var botMention = $"<@{gatewayClient.Id}>";
        var followUpText = message.Content.Replace(botMention, "").Trim();
        conversation.Add(new GeminiMessage("user", followUpText));

        var attachments = ExtractAttachments(message);
        // Reply-to-bot stays in-place: continuation of an existing conversation, no thread spawn.
        // If the bot's previous message was inside a thread, message.ChannelId already is that thread.
        await RespondWithConversation(message, message.ChannelId, conversation, attachments, ct: ct);
    }

    private async Task HandleInvocationOnBehalf(Message message, RestMessage targetMessage, CancellationToken ct = default)
    {
        var budget = await rateLimiterService.CheckBudgetAsync(message.Author.Id, ct);
        if (!budget.Allowed)
        {
            await NotifyBudgetExceeded(message, budget, ct);
            return;
        }

        // Gather context from the target user's messages, always including the replied-to message
        var (conversation, attachments) = await GatherUserContext(message, targetMessage.Author.Id, targetMessage, ct);

        var responseChannelId = await EnsureThreadAsync(message, ct);
        await RespondWithConversation(message, responseChannelId, conversation, attachments, ct: ct);
    }

    private async Task HandleDirectMention(Message message, CancellationToken ct = default)
    {
        var budget = await rateLimiterService.CheckBudgetAsync(message.Author.Id, ct);
        if (!budget.Allowed)
        {
            await NotifyBudgetExceeded(message, budget, ct);
            return;
        }

        // Gather context from the invoking user's messages only
        var (conversation, attachments) = await GatherUserContext(message, message.Author.Id, ct: ct);

        var responseChannelId = await EnsureThreadAsync(message, ct);
        await RespondWithConversation(message, responseChannelId, conversation, attachments, ct: ct);
    }

    // Returns the channel id the bot should post into. If the invoking message is in a
    // regular text channel, a thread is spawned on the user's message and that thread's id
    // is returned. If already inside a thread (or a forum/voice channel where threads don't
    // apply), the original channel id is returned. Thread name is derived from the user's
    // question, truncated to fit Discord's 100-char limit.
    private async Task<ulong> EnsureThreadAsync(Message message, CancellationToken ct)
    {
        if (IsThreadChannel(message))
            return message.ChannelId;

        // GuildThread inherits from TextGuildChannel, but the IsThreadChannel guard above
        // already filtered those. Anything that isn't a regular text/announcement channel
        // (DM, voice, forum, category) is left alone.
        if (message.Channel is not TextGuildChannel)
            return message.ChannelId;

        try
        {
            var botMention = $"<@{gatewayClient.Id}>";
            var stripped = message.Content.Replace(botMention, "").Trim();
            var threadName = BuildThreadName(stripped);
            var thread = await restClient.CreateGuildThreadAsync(
                message.ChannelId,
                message.Id,
                new GuildThreadFromMessageProperties(threadName),
                cancellationToken: ct);
            return thread.Id;
        }
        catch (Exception ex)
        {
            // If thread creation fails (permissions, archived, etc.), fall back to replying
            // in the parent channel rather than dropping the user's question on the floor.
            LogThreadCreationFailed(ex, message.ChannelId);
            return message.ChannelId;
        }
    }

    private static string BuildThreadName(string question)
    {
        var trimmed = question.Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "qBitBot question";
        return trimmed.Length <= 80 ? trimmed : trimmed[..77] + "...";
    }

    private async Task<(List<GeminiMessage> Conversation, List<AttachmentInfo> Attachments)> GatherUserContext(
        Message invokingMessage, ulong contextUserId, RestMessage? anchorMessage = null, CancellationToken ct = default)
    {
        var botUserId = gatewayClient.Id;

        // Fetch recent channel messages
        var recentMessages = await restClient.GetMessagesAroundAsync(invokingMessage.ChannelId, invokingMessage.Id, 50, null, ct);
        var now = DateTimeOffset.UtcNow;

        // IDs to exclude from the time-filtered search (handled separately)
        HashSet<ulong> excludeIds = [invokingMessage.Id];
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
        List<string> contextParts = [];
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

        // Build the conversation as: background context (user) -> ack (model) -> current question (user)
        List<GeminiMessage> conversation = [];

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

    private async Task RespondWithConversation(Message message, ulong targetChannelId, List<GeminiMessage> conversation, List<AttachmentInfo> attachments, bool isDirectInvocation = true, CancellationToken ct = default)
    {
        // Reply references only work inside the same channel as the original message.
        // When responding inside a spawned thread, the thread itself anchors the conversation.
        var sameChannel = targetChannelId == message.ChannelId;

        // Post a placeholder right away so the user sees the bot has accepted the question
        // while Gemini runs. The placeholder gets edited in-place with the final response.
        RestMessage? placeholder = null;
        try
        {
            var placeholderProps = new MessageProperties
            {
                Embeds = [PlaceholderEmbed()]
            };
            if (sameChannel)
                placeholderProps.MessageReference = MessageReferenceProperties.Reply(message.Id);
            placeholder = await restClient.SendMessageAsync(targetChannelId, placeholderProps, null, ct);
        }
        catch (Exception ex)
        {
            LogPlaceholderSendFailed(ex);
            // Fall through — we can still try to send the final response even without a placeholder.
        }

        try
        {
            using var typing = restClient.EnterTypingScope(targetChannelId);
            var result = await geminiService.AskAsync(conversation, attachments, ct);

            if (result.IsFailure || result.Value is null)
            {
                if (placeholder is not null)
                    await TryReplaceWithError(targetChannelId, placeholder.Id, ct);
                return;
            }

            var geminiResponse = result.Value;
            var prompt = conversation.LastOrDefault(t => t.Role == "user")?.Content ?? string.Empty;

            // Rejection: edit placeholder to the rejection embed. Still persisted so it
            // counts toward the daily turn budget.
            if (!geminiResponse.ShouldRespond)
            {
                if (!isDirectInvocation)
                {
                    if (placeholder is not null)
                        await TryDeletePlaceholder(targetChannelId, placeholder.Id, ct);
                    return;
                }

                var rejection = geminiResponse.IsPiracy
                    ? "Sorry, I can't help with that. I'm only able to assist with qBitTorrent client questions — topics related to piracy or illegal downloads are outside my scope."
                    : "That doesn't seem to be a qBitTorrent question. I can help with qBitTorrent client configuration, troubleshooting, and usage — feel free to ask!";
                var rejectionEmbed = new EmbedProperties
                {
                    Description = rejection,
                    Color = new Color(158, 158, 158),
                    Footer = EmbedResponseFormatter.Footer
                };

                ulong rejectionMessageId;
                if (placeholder is not null)
                {
                    await restClient.ModifyMessageAsync(targetChannelId, placeholder.Id, opts =>
                    {
                        opts.Embeds = [rejectionEmbed];
                        opts.Components = [];
                    }, null, ct);
                    rejectionMessageId = placeholder.Id;
                }
                else
                {
                    var rejectionProps = new MessageProperties { Embeds = [rejectionEmbed] };
                    if (sameChannel)
                        rejectionProps.MessageReference = MessageReferenceProperties.Reply(message.Id);
                    var rejectionMsg = await restClient.SendMessageAsync(targetChannelId, rejectionProps, null, ct);
                    rejectionMessageId = rejectionMsg.Id;
                }

                await feedbackService.RecordResponseAsync(
                    geminiResponse, prompt, rejectionMessageId,
                    targetChannelId, message.Author.Id, message.GuildId, ct);
                return;
            }

            // On-topic: edit placeholder with first chunk; if multi-embed, send the rest as
            // new messages. Buttons live on the LAST message; that's the one whose id is
            // persisted so FeedbackButtonHandler / Why can look it up.
            var responseMessages = EmbedResponseFormatter.FormatEmbedResponse(geminiResponse);
            ulong feedbackMessageId;

            if (placeholder is not null)
            {
                var first = responseMessages[0];
                await restClient.ModifyMessageAsync(targetChannelId, placeholder.Id, opts =>
                {
                    opts.Embeds = first.Embeds;
                    opts.Components = responseMessages.Count == 1 ? first.Components : [];
                }, null, ct);
                feedbackMessageId = placeholder.Id;

                for (var i = 1; i < responseMessages.Count; i++)
                {
                    var sent = await restClient.SendMessageAsync(targetChannelId, responseMessages[i], null, ct);
                    if (i == responseMessages.Count - 1)
                        feedbackMessageId = sent.Id;
                }
            }
            else
            {
                RestMessage? lastSent = null;
                for (var i = 0; i < responseMessages.Count; i++)
                {
                    if (i == 0 && sameChannel)
                        responseMessages[i].MessageReference = MessageReferenceProperties.Reply(message.Id);
                    lastSent = await restClient.SendMessageAsync(targetChannelId, responseMessages[i], null, ct);
                }
                if (lastSent is null) return;
                feedbackMessageId = lastSent.Id;
            }

            await feedbackService.RecordResponseAsync(
                geminiResponse,
                prompt,
                feedbackMessageId,
                targetChannelId,
                message.Author.Id,
                message.GuildId,
                ct);
        }
        catch (Exception ex)
        {
            LogResponseFailed(ex, message.Id, message.Author.Id);
            if (placeholder is not null)
                await TryReplaceWithError(targetChannelId, placeholder.Id, ct);
            else
                await SendErrorReply(message.ChannelId, message.Id, "direct invocation", ex, ct);
        }
    }

    private static EmbedProperties PlaceholderEmbed() => new()
    {
        Description = "_Looking into this..._",
        Color = new Color(120, 144, 156) // blue-grey
    };

    private async Task TryReplaceWithError(ulong channelId, ulong messageId, CancellationToken ct)
    {
        try
        {
            var contact = botConfig.Value.ErrorContactHandle;
            await restClient.ModifyMessageAsync(channelId, messageId, opts =>
            {
                opts.Embeds = [new EmbedProperties
                {
                    Description = $"Something went wrong while processing your request. Please ping {contact} if this keeps happening.",
                    Color = new Color(229, 57, 53)
                }];
                opts.Components = [];
            }, null, ct);
        }
        catch (Exception ex)
        {
            LogErrorReplyFailed(ex);
        }
    }

    private async Task TryDeletePlaceholder(ulong channelId, ulong messageId, CancellationToken ct)
    {
        try
        {
            await restClient.DeleteMessageAsync(channelId, messageId, null, ct);
        }
        catch (Exception ex)
        {
            LogPlaceholderDeleteFailed(ex);
        }
    }

    private static bool IsBotMentioned(Message message, ulong botUserId) =>
        message.MentionedUsers.Any(u => u.Id == botUserId);

    private static List<AttachmentInfo> ExtractAttachments(RestMessage message) =>
        message.Attachments
            .Where(a => a.ContentType is not null)
            .Select(a => new AttachmentInfo(a.Url, a.ContentType!))
            .ToList();

    private async Task NotifyBudgetExceeded(Message message, BudgetCheck budget, CancellationToken ct = default)
    {
        try
        {
            var resetIn = budget.ResetAt.HasValue
                ? budget.ResetAt.Value - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
            var resetStr = resetIn >= TimeSpan.FromHours(1)
                ? $"{(int)resetIn.TotalHours}h {resetIn.Minutes}m"
                : $"{Math.Max(1, (int)resetIn.TotalMinutes)}m";

            await restClient.SendMessageAsync(message.ChannelId, new MessageProperties
            {
                Content = $"You've used **{budget.Used}/{budget.Limit}** qBitBot requests in the last 24 hours. "
                        + $"Your next slot opens in **{resetStr}**.\n"
                        + "For longer discussions, try [Gemini](<https://gemini.google.com/>) or [Claude](<https://claude.ai/>) directly.",
                MessageReference = MessageReferenceProperties.Reply(message.Id)
            }, null, ct);
        }
        catch (Exception ex)
        {
            LogBudgetNotificationFailed(ex);
        }
    }

    private async Task SendErrorReply(ulong channelId, ulong replyToMessageId, string context, Exception ex, CancellationToken ct = default)
    {
        try
        {
            var topFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
            var errorInfo = $"`{ex.GetType().Name}: {topFrame}`";
            var contact = botConfig.Value.ErrorContactHandle;

            await restClient.SendMessageAsync(channelId, new MessageProperties
            {
                Content = $"Something went wrong while processing your request ({context}). Please ping {contact} if this keeps happening.\n-# {errorInfo}",
                MessageReference = MessageReferenceProperties.Reply(replyToMessageId)
            }, null, ct);
        }
        catch (Exception replyEx)
        {
            LogErrorReplyFailed(replyEx);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to respond to message {MessageId} from user {UserId}")]
    private partial void LogResponseFailed(Exception ex, ulong messageId, ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send budget-exceeded notification")]
    private partial void LogBudgetNotificationFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create thread in channel {ChannelId} — falling back to in-channel reply")]
    private partial void LogThreadCreationFailed(Exception ex, ulong channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send placeholder message")]
    private partial void LogPlaceholderSendFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete placeholder after off-topic / non-direct invocation")]
    private partial void LogPlaceholderDeleteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send error reply")]
    private partial void LogErrorReplyFailed(Exception ex);
}
