namespace qBitBotNew.Persistence.Entities;

public sealed class ResponseCache
{
    public long Id { get; set; }

    public ulong ChannelId { get; set; }
    public ulong BotMessageId { get; set; }

    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;

    // Packed float[] of the prompt embedding (text-embedding-004 → 768 dims × 4 bytes = 3072 bytes).
    // Used for cosine similarity lookup before issuing a fresh Gemini call.
    public byte[] Embedding { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
