namespace qBitBotNew.Config;

public sealed record BotConfig
{
    public int NewUserThresholdHours { get; init; } = 24;

    // New-user greeting: after a user that joined < NewUserThresholdHours ago posts in a
    // channel and goes silent for GreetWaitMinutes minutes (and nobody replies to them),
    // the bot offers an opt-in button to answer their question.
    public bool GreetEnabled { get; init; } = true;
    public int GreetWaitMinutes { get; init; } = 10;

    // Sliding 24h budget per user, counted from the Feedback table (every Gemini call).
    // Exceeding it points the user at Gemini / Claude directly for long discussions.
    public int DailyTurnBudget { get; init; } = 20;

    public string ErrorContactHandle { get; init; } = "@ayymoss";
    public long MaxAttachmentBytes { get; init; } = 10 * 1024 * 1024; // 10 MB
}
