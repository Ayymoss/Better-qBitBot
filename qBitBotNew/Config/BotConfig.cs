namespace qBitBotNew.Config;

public sealed class BotConfig
{
    public int NewUserThresholdHours { get; set; } = 24;
    public int MessageAggregationWindowSeconds { get; set; } = 60;
    public int CooldownSeconds { get; set; } = 60;
    public string ErrorContactHandle { get; set; } = "@ayymoss";
    public long MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024; // 10 MB
}
