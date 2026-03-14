namespace qBitBotNew.Models;

public sealed class PendingQuestion
{
    public required ulong UserId { get; init; }
    public required ulong ChannelId { get; init; }
    public required ulong GuildId { get; init; }
    public required ulong FirstMessageId { get; init; }
    public List<string> MessageContents { get; } = [];
    public List<AttachmentInfo> Attachments { get; } = [];
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public Timer? AggregationTimer { get; set; }
    public bool Intervened { get; set; }
}

public sealed record AttachmentInfo(string Url, string ContentType);
