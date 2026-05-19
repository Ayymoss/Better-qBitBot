using qBitBotNew.Models;

namespace qBitBotNew.Persistence.Entities;

public sealed class FeedbackEntry
{
    public long Id { get; set; }

    // Discord message ID of the bot's response message. Used for button-click lookup.
    public ulong BotMessageId { get; set; }

    public ulong UserId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong? GuildId { get; set; }

    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
    public ConfidenceLevel Confidence { get; set; }

    // Null until the user clicks a feedback button.
    public bool? Helpful { get; set; }

    // Free-text reason from the "Not Helpful" modal. Null when not provided.
    public string? Reason { get; set; }

    public string ThoughtSummary { get; set; } = string.Empty;

    public int PromptTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int ThoughtTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RatedAt { get; set; }
}
