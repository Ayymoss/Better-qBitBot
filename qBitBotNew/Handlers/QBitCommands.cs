using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using qBitBotNew.Helpers;
using qBitBotNew.Models;
using qBitBotNew.Services;

namespace qBitBotNew.Handlers;

public sealed class QBitCommands(GeminiService geminiService, FeedbackService feedbackService) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("qbit-stats", "Show qBitBot usage statistics")]
    public async Task Stats()
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        var s = await feedbackService.GetStatsAsync();

        var helpfulPct = s.RatedCount > 0 ? s.HelpfulCount * 100.0 / s.RatedCount : 0;
        var conf7d = s.HighCount7d + s.MediumCount7d + s.LowCount7d;

        var lowList = s.LowConfidencePrompts7d.Count == 0
            ? "_None — Gemini was confident in every answer this week._"
            : string.Join("\n", s.LowConfidencePrompts7d.Select(p =>
            {
                var trimmed = p.Replace('\n', ' ').Trim();
                return trimmed.Length > 100 ? "- " + trimmed[..97] + "..." : "- " + trimmed;
            }));

        var embed = new EmbedProperties
        {
            Title = "qBitBot stats",
            Color = new Color(67, 160, 71),
            Fields =
            [
                new EmbedFieldProperties
                {
                    Name = "Responses",
                    Value = $"24h: **{s.ResponsesLast24h}** • 7d: **{s.ResponsesLast7d}** • All: **{s.ResponsesAll}**",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Feedback",
                    Value = s.RatedCount == 0
                        ? "_No ratings yet._"
                        : $"👍 **{s.HelpfulCount}** / 👎 **{s.RatedCount - s.HelpfulCount}** ({helpfulPct:0.0}% helpful, {s.RatedCount} rated)",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Confidence (7d)",
                    Value = conf7d == 0
                        ? "_No on-topic responses this week._"
                        : $"High: **{s.HighCount7d}** • Medium: **{s.MediumCount7d}** • Low: **{s.LowCount7d}**",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Tokens (7d)",
                    Value = $"Prompt: **{s.PromptTokens7d:N0}** • Cached: **{s.CachedTokens7d:N0}** • Output: **{s.OutputTokens7d:N0}** • Thoughts: **{s.ThoughtTokens7d:N0}**",
                    Inline = false
                },
                new EmbedFieldProperties
                {
                    Name = "Recent low-confidence prompts (7d)",
                    Value = lowList.Length > 1024 ? lowList[..1021] + "..." : lowList,
                    Inline = false
                }
            ],
            Footer = new EmbedFooterProperties { Text = "Only on-topic responses are tracked. Times are UTC." }
        };

        await FollowupAsync(new InteractionMessageProperties
        {
            Embeds = [embed],
            Flags = MessageFlags.Ephemeral
        });
    }

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
