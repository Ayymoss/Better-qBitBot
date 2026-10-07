using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using qBitBotNew.Helpers;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class GreetButtonHandler(
    ClaudeService claudeService,
    FeedbackService feedbackService,
    RateLimiterService rateLimiterService,
    RestClient restClient,
    GatewayClient gatewayClient) : ComponentInteractionModule<ButtonInteractionContext>
{
    // Custom id format: greet_invoke:{userId}:{channelId}:{anchorMessageId}
    [ComponentInteraction("greet_invoke")]
    public async Task Invoke(ulong userId, ulong channelId, ulong anchorMessageId)
    {
        // Only the targeted user can opt in — prevents anyone from triggering Claude
        // calls on someone else's behalf and burning their daily budget.
        if (Context.User.Id != userId)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties
            {
                Content = "Only the user this offer was made to can accept it.",
                Flags = MessageFlags.Ephemeral
            }));
            return;
        }

        // Budget check still applies — greet is just an invocation path.
        var budget = await rateLimiterService.CheckBudgetAsync(userId);
        if (!budget.Allowed)
        {
            await RespondAsync(InteractionCallback.Message(new InteractionMessageProperties
            {
                Content = $"You've used **{budget.Used}/{budget.Limit}** qBitBot requests in the last 24 hours. "
                        + "Try again later or use Gemini / Claude directly for now.",
                Flags = MessageFlags.Ephemeral
            }));
            return;
        }

        // Defer — also clears the buttons on the greet message so it can't be clicked twice.
        await RespondAsync(InteractionCallback.DeferredModifyMessage);

        try
        {
            await DoGreetAnswerAsync(userId, channelId, anchorMessageId, Context.Guild?.Id);

            await ModifyResponseAsync(opts =>
            {
                opts.Components = [];
                opts.Embeds = [new EmbedProperties
                {
                    Description = "Answered in a thread above. Please continue the conversation there.",
                    Color = new Color(67, 160, 71)
                }];
            });
        }
        catch (Exception)
        {
            await ModifyResponseAsync(opts =>
            {
                opts.Components = [];
                opts.Embeds = [new EmbedProperties
                {
                    Description = "Something went wrong trying to answer. Please try @mentioning me directly.",
                    Color = new Color(229, 57, 53)
                }];
            });
            throw;
        }
    }

    private async Task DoGreetAnswerAsync(ulong userId, ulong channelId, ulong anchorMessageId, ulong? guildId)
    {
        var anchor = await restClient.GetMessageAsync(channelId, anchorMessageId);

        // Gather surrounding context — same shape as MessageCreateHandler.GatherUserContext
        // but driven off RestMessage primitives since we don't have a gateway Message here.
        var recentMessages = await restClient.GetMessagesAroundAsync(channelId, anchorMessageId, 50);
        var now = DateTimeOffset.UtcNow;
        var contextMessages = recentMessages
            .Where(m => m.Id != anchorMessageId)
            .Where(m => now - m.CreatedAt < TimeSpan.FromHours(12))
            .OrderBy(m => m.Id)
            .ToList();

        var anchorName = GetDisplayName(anchor.Author);
        var anchorTime = anchor.CreatedAt.ToString("HH:mm");

        List<string> contextParts =
        [
            $"[Primary question — this is the message the user wanted answered]:\n[{anchorTime}] {anchorName}: {anchor.Content}"
              + (anchor.Attachments.Any() ? " [has attached image]" : "")
        ];

        if (contextMessages.Count > 0)
        {
            contextParts.Add("[Surrounding context — recent messages in the channel]");
            foreach (var m in contextMessages)
            {
                var time = m.CreatedAt.ToString("HH:mm");
                var name = m.Author.Id == gatewayClient.Id ? "qBitBot (you)" : GetDisplayName(m.Author);
                var content = m.Author.Id == gatewayClient.Id
                    ? m.Embeds.FirstOrDefault()?.Description ?? m.Content
                    : m.Content;
                contextParts.Add($"[{time}] {name}: {content}{(m.Attachments.Any() ? " [has attached image]" : "")}");
            }
        }

        List<ChatMessage> conversation =
        [
            new("user", string.Join("\n", contextParts)),
            new("assistant", "Understood. I've read the conversation context. What's the question?"),
            new("user", "Answer the primary question from the context above.")
        ];

        var attachments = anchor.Attachments
            .Where(a => a.ContentType is not null)
            .Select(a => new AttachmentInfo(a.Url, a.ContentType!))
            .ToList();

        // Spawn a thread on the anchor message so the answer stays out of the parent channel.
        var responseChannelId = channelId;
        ulong? spawnedThreadId = null;
        try
        {
            var thread = await restClient.CreateGuildThreadAsync(
                channelId, anchorMessageId,
                new GuildThreadFromMessageProperties(ThreadNaming.Build(anchor.Content)));
            responseChannelId = thread.Id;
            spawnedThreadId = thread.Id;
        }
        catch
        {
            // Fall back to posting in the parent channel.
        }

        // Placeholder so the thread isn't empty during the Claude wait.
        var placeholder = await restClient.SendMessageAsync(responseChannelId, new MessageProperties
        {
            Embeds = [EmbedResponseFormatter.BuildPlaceholderEmbed()]
        });

        var result = await claudeService.AskAsync(conversation, attachments);
        if (result.IsFailure || result.Value is null)
        {
            await restClient.ModifyMessageAsync(responseChannelId, placeholder.Id, opts =>
            {
                opts.Embeds = [new EmbedProperties
                {
                    Description = "Something went wrong — couldn't get a response.",
                    Color = new Color(229, 57, 53)
                }];
            });
            return;
        }

        var botResponse = result.Value;
        // Persist the anchor message, not the synthetic "Answer the primary question…" turn.
        var prompt = $"[{anchorTime}] {anchorName}: {anchor.Content}";

        if (!botResponse.ShouldRespond)
        {
            var rejection = botResponse.IsPiracy
                ? "Sorry, I can't help with that. I'm only able to assist with qBitTorrent client questions."
                : "That doesn't look like a qBitTorrent question — I can only help with the client itself.";
            await restClient.ModifyMessageAsync(responseChannelId, placeholder.Id, opts =>
            {
                opts.Embeds = [new EmbedProperties
                {
                    Description = rejection,
                    Color = new Color(158, 158, 158),
                    Footer = EmbedResponseFormatter.Footer
                }];
            });
            await feedbackService.RecordResponseAsync(
                botResponse, prompt, placeholder.Id, responseChannelId, userId, guildId);
            return;
        }

        var embeds = EmbedResponseFormatter.BuildEmbeds(botResponse);
        await restClient.ModifyMessageAsync(responseChannelId, placeholder.Id, opts =>
        {
            opts.Embeds = embeds;
            opts.Components = [EmbedResponseFormatter.FeedbackButtons];
        });

        await feedbackService.RecordResponseAsync(
            botResponse, prompt, placeholder.Id, responseChannelId, userId, guildId);

        if (spawnedThreadId is not null && !string.IsNullOrWhiteSpace(botResponse.Topic))
            await ThreadNaming.TryRenameAsync(restClient, spawnedThreadId.Value, ThreadNaming.Build(botResponse.Topic));
    }

    private static string GetDisplayName(User author) =>
        (author as GuildUser)?.Nickname ?? author.GlobalName ?? author.Username;
}
