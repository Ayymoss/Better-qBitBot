using NetCord;
using NetCord.Rest;
using qBitBotNew.Models;

namespace qBitBotNew.Helpers;

public static class EmbedResponseFormatter
{
    private const string VerifyHint = "Generated response — please verify before applying.";

    // Verify text appears multiple times in the pool so it lands ~50% of the time;
    // other hints surface less often to educate users without nagging.
    private static readonly string[] FooterHints =
    [
        VerifyHint,
        VerifyHint,
        VerifyHint,
        VerifyHint,
        "Tip: reply to this message to ask a follow-up.",
        "Tip: use /qbit <question> for one-off questions.",
        "Tip: react 👍 / 👎 to rate this answer.",
        "Tip: right-click any message → Apps → Ask qBitBot."
    ];

    // Static footer kept for rejection / cooldown embeds where rotating hints would feel off.
    public static readonly EmbedFooterProperties Footer = new() { Text = VerifyHint };

    public static EmbedFooterProperties BuildHintFooter() =>
        new() { Text = FooterHints[Random.Shared.Next(FooterHints.Length)] };

    public static readonly ActionRowProperties FeedbackButtons = new([
        new ButtonProperties("feedback_helpful", "Helpful", ButtonStyle.Success),
        new ButtonProperties("feedback_not_helpful", "Not Helpful", ButtonStyle.Danger),
        new ButtonProperties("feedback_why", "Why this answer?", ButtonStyle.Secondary)
    ]);

    private const int MaxEmbedDescription = 4096;

    public static Color GetConfidenceColor(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => new Color(67, 160, 71),     // green
        ConfidenceLevel.Medium => new Color(251, 192, 45),  // amber
        _ => new Color(229, 57, 53)                          // red — distinct from medium amber
    };

    public static string BuildResponseText(GeminiResponse result)
    {
        var text = result.Response.Replace("\\n", "\n");

        if (result.Confidence is ConfidenceLevel.Low)
            text = "I'm not entirely sure about this, but here are some resources that might help:";

        if (result.Resources is { Count: > 0 })
            text += "\n\n**Resources:**\n" + string.Join("\n", result.Resources.Select(r => $"- <{r}>"));

        return text;
    }

    // Follow-up questions get their own embed field so they stand out — users were ignoring
    // them when appended to the description paragraph.
    public static EmbedFieldProperties? BuildFollowUpField(GeminiResponse result)
    {
        if (result.FollowUpQuestions is not { Count: > 0 } qs)
            return null;

        var value = string.Join("\n", qs.Select(q => $"- {q}"));
        // Discord field value max is 1024 chars. Truncate defensively.
        if (value.Length > 1024)
            value = value[..1021] + "...";

        return new EmbedFieldProperties
        {
            Name = "To help further, please share:",
            Value = value,
            Inline = false
        };
    }

    public static List<MessageProperties> FormatEmbedResponse(GeminiResponse result)
    {
        var text = BuildResponseText(result);
        var color = GetConfidenceColor(result.Confidence);
        var followUpField = BuildFollowUpField(result);

        if (text.Length <= MaxEmbedDescription)
        {
            var embed = new EmbedProperties { Description = text, Color = color, Footer = BuildHintFooter() };
            if (followUpField is not null)
                embed.Fields = [followUpField];
            return [new MessageProperties
            {
                Embeds = [embed],
                Components = [FeedbackButtons]
            }];
        }

        List<MessageProperties> messages = [];
        var remaining = text;

        while (remaining.Length > 0)
        {
            var isLast = remaining.Length <= MaxEmbedDescription;
            string chunk;

            if (isLast)
            {
                chunk = remaining;
                remaining = "";
            }
            else
            {
                var splitAt = remaining.LastIndexOf('\n', MaxEmbedDescription - 1);
                if (splitAt <= 0) splitAt = MaxEmbedDescription;
                chunk = remaining[..splitAt];
                remaining = remaining[splitAt..].TrimStart('\n');
            }

            var embed = new EmbedProperties { Description = chunk, Color = color };
            var props = new MessageProperties { Embeds = [embed] };

            if (isLast || remaining.Length == 0)
            {
                embed.Footer = BuildHintFooter();
                if (followUpField is not null)
                    embed.Fields = [followUpField];
                props.Components = [FeedbackButtons];
            }

            messages.Add(props);
        }

        return messages;
    }

    public static EmbedProperties BuildSingleEmbed(GeminiResponse result)
    {
        var text = BuildResponseText(result);
        var color = GetConfidenceColor(result.Confidence);
        var followUpField = BuildFollowUpField(result);

        var embed = new EmbedProperties
        {
            Description = text.Length > MaxEmbedDescription ? text[..4093] + "..." : text,
            Color = color,
            Footer = BuildHintFooter()
        };
        if (followUpField is not null)
            embed.Fields = [followUpField];
        return embed;
    }
}
