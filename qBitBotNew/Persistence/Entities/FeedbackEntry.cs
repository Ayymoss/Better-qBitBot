using qBitBotNew.Models;

namespace qBitBotNew.Persistence.Entities;

public sealed class FeedbackEntry
{
    public long Id { get; set; }

    // Discord snowflakes are stored as signed long because EF Core's SQLite provider can't
    // translate LINQ expressions involving ulong. They fit comfortably in 63 bits.
    // Callers cast at the boundary: unchecked((long)ulongId).
    public long BotMessageId { get; set; }

    public long UserId { get; set; }
    public long ChannelId { get; set; }
    public long? GuildId { get; set; }

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
