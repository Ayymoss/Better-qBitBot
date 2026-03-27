using NetCord;
using NetCord.Rest;
using qBitBotNew.Models;

namespace qBitBotNew.Helpers;

public static class EmbedResponseFormatter
{
    public static readonly EmbedFooterProperties Footer = new() { Text = "Generated response — please verify before applying." };

    public static readonly ActionRowProperties FeedbackButtons = new([
        new ButtonProperties("feedback_helpful", "Helpful", ButtonStyle.Success),
        new ButtonProperties("feedback_not_helpful", "Not Helpful", ButtonStyle.Danger)
    ]);

    private const int MaxEmbedDescription = 4096;

    public static Color GetConfidenceColor(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => new Color(67, 160, 71),
        ConfidenceLevel.Medium => new Color(251, 192, 45),
        _ => new Color(255, 152, 0)
    };

    public static string BuildResponseText(GeminiResponse result)
    {
        var text = result.Response.Replace("\\n", "\n");

        if (result.Confidence is ConfidenceLevel.Low)
            text = "I'm not entirely sure about this, but here are some resources that might help:";

        if (result.Resources is { Count: > 0 })
            text += "\n\n**Resources:**\n" + string.Join("\n", result.Resources.Select(r => $"- <{r}>"));

        if (result.FollowUpQuestions is { Count: > 0 })
            text += "\n\n**To help further, please share:**\n"
                  + string.Join("\n", result.FollowUpQuestions.Select(q => $"- {q}"));

        return text;
    }

    public static List<MessageProperties> FormatEmbedResponse(GeminiResponse result)
    {
        var text = BuildResponseText(result);
        var color = GetConfidenceColor(result.Confidence);

        if (text.Length <= MaxEmbedDescription)
        {
            return [new MessageProperties
            {
                Embeds = [new EmbedProperties { Description = text, Color = color, Footer = Footer }],
                Components = [FeedbackButtons]
            }];
        }

        var messages = new List<MessageProperties>();
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
                embed.Footer = Footer;
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

        return new EmbedProperties
        {
            Description = text.Length > MaxEmbedDescription ? text[..4093] + "..." : text,
            Color = color,
            Footer = Footer
        };
    }
}
