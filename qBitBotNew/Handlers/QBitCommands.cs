using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using qBitBotNew.Helpers;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class QBitCommands(GeminiService geminiService, FeedbackService feedbackService) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("help", "How to use qBitBot")]
    public Task Help()
    {
        var embed = new EmbedProperties
        {
            Title = "qBitBot — how to ask",
            Description =
                "I answer **qBitTorrent client** questions (config, troubleshooting, WebUI, API). "
              + "Piracy and off-topic questions are declined.",
            Color = new Color(67, 160, 71),
            Fields =
            [
                new EmbedFieldProperties
                {
                    Name = "Mention me",
                    Value = "`@qBitBot why won't my torrent seed?` — I'll pull in nearby messages and any screenshots for context.",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Reply to my message",
                    Value = "Reply to one of my answers to ask a follow-up. I'll remember the chain.",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "/qbit <question>",
                    Value = "One-off question. No surrounding context, just your text.",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Right-click a message → Apps → Ask qBitBot",
                    Value = "Run me against someone else's message + any attached screenshots.",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Rate my answers",
                    Value = "Use the 👍 / 👎 buttons. Feedback helps improve future responses.",
                    Inline = false
                }
            ],
            Footer = new EmbedFooterProperties { Text = "Cooldown applies per user. Be kind." }
        };

        return RespondAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Embeds = [embed],
            Flags = MessageFlags.Ephemeral
        }));
    }

    [SlashCommand("qbit", "Ask a qBitTorrent question")]
    public async Task Ask(
        [SlashCommandParameter(Name = "question", Description = "Your qBitTorrent question")] string question)
    {
        // Defer since Gemini takes a while
        await RespondAsync(InteractionCallback.DeferredMessage());

        var result = await geminiService.AskAsync([new GeminiMessage("user", question)]);

        if (result.IsFailure || result.Value is null)
        {
            await FollowupAsync(new InteractionMessageProperties
            {
                Content = "Something went wrong — couldn't get a response. Try again later."
            });
            return;
        }

        var geminiResponse = result.Value;

        if (!geminiResponse.ShouldRespond)
        {
            var rejection = geminiResponse.IsPiracy
                ? "Sorry, I can't help with that. I'm only able to assist with qBitTorrent client questions — topics related to piracy or illegal downloads are outside my scope."
                : "That doesn't seem to be a qBitTorrent question. I can help with qBitTorrent client configuration, troubleshooting, and usage — feel free to ask!";

            await FollowupAsync(new InteractionMessageProperties
            {
                Embeds = [new EmbedProperties
                {
                    Description = rejection,
                    Color = new Color(158, 158, 158),
                    Footer = EmbedResponseFormatter.Footer
                }]
            });
            return;
        }

        var embed = EmbedResponseFormatter.BuildSingleEmbed(geminiResponse);
        var sent = await FollowupAsync(new InteractionMessageProperties
        {
            Embeds = [embed],
            Components = [EmbedResponseFormatter.FeedbackButtons]
        });

        await feedbackService.RecordResponseAsync(
            geminiResponse,
            question,
            sent.Id,
            Context.Channel.Id,
            Context.User.Id,
            Context.Guild?.Id);
    }

    [MessageCommand("Ask qBitBot")]
    public async Task AskFromMessage(RestMessage message)
    {
        await RespondAsync(InteractionCallback.DeferredMessage());

        var question = message.Content;

        if (string.IsNullOrWhiteSpace(question))
        {
            // Try embed description as fallback
            question = message.Embeds.FirstOrDefault()?.Description;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            await FollowupAsync(new InteractionMessageProperties
            {
                Content = "That message doesn't seem to have any text content to ask about."
            });
            return;
        }

        List<AttachmentInfo> attachments = message.Attachments
            .Where(a => a.ContentType is not null)
            .Select(a => new AttachmentInfo(a.Url, a.ContentType!))
            .ToList();

        var result = await geminiService.AskAsync([new GeminiMessage("user", question)], attachments);

        if (result.IsFailure || result.Value is null)
        {
            await FollowupAsync(new InteractionMessageProperties
            {
                Content = "Something went wrong — couldn't get a response. Try again later."
            });
            return;
        }

        var geminiResponse = result.Value;

        if (!geminiResponse.ShouldRespond)
        {
            var rejection = geminiResponse.IsPiracy
                ? "Sorry, I can't help with that."
                : "That doesn't seem to be a qBitTorrent question.";

            await FollowupAsync(new InteractionMessageProperties
            {
                Embeds = [new EmbedProperties
                {
                    Description = rejection,
                    Color = new Color(158, 158, 158),
                    Footer = EmbedResponseFormatter.Footer
                }]
            });
            return;
        }

        var embed = EmbedResponseFormatter.BuildSingleEmbed(geminiResponse);
        var sent = await FollowupAsync(new InteractionMessageProperties
        {
            Embeds = [embed],
            Components = [EmbedResponseFormatter.FeedbackButtons]
        });

        await feedbackService.RecordResponseAsync(
            geminiResponse,
            question,
            sent.Id,
            Context.Channel.Id,
            Context.User.Id,
            Context.Guild?.Id);
    }
}
