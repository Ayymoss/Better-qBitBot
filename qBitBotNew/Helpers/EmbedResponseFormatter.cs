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
        "Tip: rate this answer with the Helpful / Not Helpful buttons.",
        "Tip: right-click any message → Apps → Ask qBitBot."
    ];

    // Static footer kept for rejection / cooldown embeds where rotating hints would feel off.
    public static readonly EmbedFooterProperties Footer = new() { Text = VerifyHint };

    public static EmbedFooterProperties BuildHintFooter() =>
        new() { Text = FooterHints[Random.Shared.Next(FooterHints.Length)] };

    // Shown while Claude is generating. Mix of progress phrases and quick tips so the
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
        "_Tip while you wait: rate my answer with the buttons under it — it helps tune future answers._",
        "_Tip while you wait: right-click any message → Apps → Ask qBitBot._",
        "_Tip while you wait: my responses are AI-generated — verify before applying._",
        "_Have a screenshot of your settings? Attach it next time — I can read images._",
        "_Pro tip: in a thread with me, you can ping me without `@`-mentioning._"
    ];

    public static bool IsPlaceholder(string description) => PlaceholderLines.Contains(description);

    public const string QuestionsTitle = "❓ To help further, please share";

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

    // Discord rejects a whole message (400 MAX_EMBED_SIZE_EXCEEDED) when the combined title,
    // description, field and footer text of all its embeds exceeds 6000 characters.
    private const int MaxMessageEmbedChars = 6000;
    private const int MaxQuestionsChars = 1500;
    private const int MaxResourcesChars = 1000;
    private const string TrimmedNote = "\n\n_…answer trimmed to fit Discord. Reply to ask for the rest._";

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
    /// The answer gets whatever is left of Discord's 6000-char per-message embed budget after the
    /// questions, resources and footer, is trimmed at a line break if it doesn't fit, and is split
    /// across embeds at 4096 chars. Footer hint lands on the LAST embed. Caller attaches
    /// FeedbackButtons to the message itself.
    /// </summary>
    public static List<EmbedProperties> BuildEmbeds(BotResponse result)
    {
        var answerColor = GetConfidenceColor(result.Confidence);

        var answerText = result.Confidence is ConfidenceLevel.Low
            ? "I'm not entirely sure about this, but here are some resources that might help:"
            : result.Response.Replace("\\n", "\n");

        List<EmbedProperties> trailing = [];

        // Questions embed — bold-numbered list in the description so each "N. text" stays
        // on one line. Field name/value is always two-line in Discord, which read oddly.
        if (result.FollowUpQuestions is { Count: > 0 } qs)
        {
            trailing.Add(new EmbedProperties
            {
                Title = QuestionsTitle,
                Color = QuestionsColor,
                Description = Truncate(string.Join("\n", qs.Select((q, i) => $"**{i + 1}.** {q}")), MaxQuestionsChars)
            });
        }

        if (result.Resources is { Count: > 0 } resources)
        {
            trailing.Add(new EmbedProperties
            {
                Title = "📚 Resources",
                Color = ResourcesColor,
                Description = Truncate(string.Join("\n", resources.Select(r => $"- <{r}>")), MaxResourcesChars)
            });
        }

        var footer = BuildHintFooter();
        var used = trailing.Sum(e => (e.Title?.Length ?? 0) + (e.Description?.Length ?? 0)) + (footer.Text?.Length ?? 0);
        answerText = FitToBudget(answerText, MaxMessageEmbedChars - used);

        List<EmbedProperties> embeds = [];
        foreach (var chunk in SplitForEmbed(answerText, MaxEmbedDescription))
            embeds.Add(new EmbedProperties { Description = chunk, Color = answerColor });
        embeds.AddRange(trailing);

        embeds[^1].Footer = footer;
        return embeds;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 4)] + "\n...";

    // Cuts at the last line break that leaves room for the note, so markdown lists stay intact.
    private static string FitToBudget(string text, int budget)
    {
        if (text.Length <= budget)
            return text;

        var room = budget - TrimmedNote.Length;
        var cut = text.LastIndexOf('\n', room - 1);
        if (cut <= 0) cut = room;
        return text[..cut].TrimEnd() + TrimmedNote;
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
