using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using qBitBotNew.Helpers;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class QBitCommands(GeminiService geminiService) : ApplicationCommandModule<ApplicationCommandContext>
{

    [SlashCommand("qbit", "Ask a qBitTorrent question")]
    public async Task Ask(
        [SlashCommandParameter(Name = "question", Description = "Your qBitTorrent question")] string question)
    {
        // Defer since Gemini takes a while
        await RespondAsync(InteractionCallback.DeferredMessage());

        var result = await geminiService.AskAsync([new GeminiMessage("user", question)]);

        if (result is null)
        {
            await FollowupAsync(new InteractionMessageProperties
            {
                Content = "Something went wrong — couldn't get a response. Try again later."
            });
            return;
        }

        if (!result.ShouldRespond)
        {
            var rejection = result.IsPiracy
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

        var embed = EmbedResponseFormatter.BuildSingleEmbed(result);
        await FollowupAsync(new InteractionMessageProperties
        {
            Embeds = [embed],
            Components = [EmbedResponseFormatter.FeedbackButtons]
        });
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

        var attachments = message.Attachments
            .Where(a => a.ContentType is not null)
            .Select(a => new AttachmentInfo(a.Url, a.ContentType!))
            .ToList();

        var result = await geminiService.AskAsync([new GeminiMessage("user", question)], attachments);

        if (result is null)
        {
            await FollowupAsync(new InteractionMessageProperties
            {
                Content = "Something went wrong — couldn't get a response. Try again later."
            });
            return;
        }

        if (!result.ShouldRespond)
        {
            var rejection = result.IsPiracy
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

        var embed = EmbedResponseFormatter.BuildSingleEmbed(result);
        await FollowupAsync(new InteractionMessageProperties
        {
            Embeds = [embed],
            Components = [EmbedResponseFormatter.FeedbackButtons]
        });
    }
}
