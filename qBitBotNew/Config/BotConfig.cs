namespace qBitBotNew.Config;

public sealed record BotConfig
{
    public int NewUserThresholdHours { get; init; } = 24;
    public int MessageAggregationWindowSeconds { get; init; } = 60;
    public int CooldownSeconds { get; init; } = 60;
    public string ErrorContactHandle { get; init; } = "@ayymoss";
    public long MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024; // 10 MB
}
