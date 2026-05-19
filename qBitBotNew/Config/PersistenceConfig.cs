namespace qBitBotNew.Config;

public sealed record PersistenceConfig
{
    public string DatabaseFile { get; init; } = "data/qbitbot.db";
}
