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

    // Shown while Gemini is generating. Mix of progress phrases and quick tips so the
    // ~10s wait feels less dead. Picked at random per request.
    private static readonly string[] PlaceholderLines =
    [
        "_Looking into this..._",
        "_Reading your question..._",
        "_Thinking..._",
        "_Checking the docs..._",
        "_One moment..._",
        "_Working on it..._",
        "_Tip while you wait: reply to my answers to ask a follow-up._",
        "_Tip while you wait: use `/qbit` for one-off questions._",
        "_Tip while you wait: react 👍 / 👎 once I'm done — it helps tune future answers._",
        "_Tip while you wait: right-click any message → Apps → Ask qBitBot._",
        "_Tip while you wait: my responses are AI-generated — verify before applying._",
        "_Have a screenshot of your settings? Attach it next time — I can read images._",
        "_Pro tip: in a thread with me, you can ping me without `@`-mentioning._"
    ];

    public static EmbedProperties BuildPlaceholderEmbed() => new()
    {
        Description = PlaceholderLines[Random.Shared.Next(PlaceholderLines.Length)],
        Color = new Color(120, 144, 156) // blue-grey
    };

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

    private static readonly Color QuestionsColor = new(255, 152, 0);   // amber
    private static readonly Color ResourcesColor = new(120, 144, 156); // blue-grey

    /// <summary>
    /// Builds the embed list for a single message: [answer..., questions?, resources?].
    /// Answer is split across multiple embeds if it exceeds 4096 chars. Footer hint lands
    /// on the LAST embed. Caller attaches FeedbackButtons to the message itself.
    /// Total embeds always stays under Discord's 10-per-message limit.
    /// </summary>
    public static List<EmbedProperties> BuildEmbeds(GeminiResponse result)
    {
        var answerColor = GetConfidenceColor(result.Confidence);

        var answerText = result.Confidence is ConfidenceLevel.Low
            ? "I'm not entirely sure about this, but here are some resources that might help:"
            : result.Response.Replace("\\n", "\n");

        List<EmbedProperties> embeds = [];

        // Answer embeds — split at \n boundaries to stay under 4096. Cap at 8 to leave room
        // for the questions + resources embeds.
        foreach (var chunk in SplitForEmbed(answerText, MaxEmbedDescription).Take(8))
        {
            embeds.Add(new EmbedProperties { Description = chunk, Color = answerColor });
        }

        // Questions embed — one field per question so each stands distinct.
        if (result.FollowUpQuestions is { Count: > 0 } qs)
        {
            var fields = qs.Select((q, i) => new EmbedFieldProperties
            {
                Name = $"{i + 1}.",
                Value = q.Length > 1024 ? q[..1021] + "..." : q,
                Inline = false
            }).Take(25).ToArray(); // Discord field cap is 25 per embed.

            embeds.Add(new EmbedProperties
            {
                Title = "❓ To help further, please share",
                Color = QuestionsColor,
                Fields = fields
            });
        }

        // Resources embed.
        if (result.Resources is { Count: > 0 } resources)
        {
            var body = string.Join("\n", resources.Select(r => $"- <{r}>"));
            if (body.Length > MaxEmbedDescription)
                body = body[..(MaxEmbedDescription - 4)] + "\n...";

            embeds.Add(new EmbedProperties
            {
                Title = "📚 Resources",
                Color = ResourcesColor,
                Description = body
            });
        }

        // Hint footer on the last embed.
        embeds[^1].Footer = BuildHintFooter();
        return embeds;
    }

    private static IEnumerable<string> SplitForEmbed(string text, int limit)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return "_(empty response)_";
            yield break;
        }

        while (text.Length > limit)
        {
            var splitAt = text.LastIndexOf('\n', limit - 1);
            if (splitAt <= 0) splitAt = limit;
            yield return text[..splitAt];
            text = text[splitAt..].TrimStart('\n');
        }
        if (text.Length > 0)
            yield return text;
    }
}
