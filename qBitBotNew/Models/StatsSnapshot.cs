namespace qBitBotNew.Models;

public sealed record StatsSnapshot(
    int ResponsesLast24h,
    int ResponsesLast7d,
    int ResponsesAll,
    int RatedCount,
    int HelpfulCount,
    int OnTopicCount7d,
    int PiracyCount7d,
    int OffTopicCount7d,
    int HighCount7d,
    int MediumCount7d,
    int LowCount7d,
    long PromptTokens7d,
    long CachedTokens7d,
    long OutputTokens7d,
    long ThoughtTokens7d,
    IReadOnlyList<string> LowConfidencePrompts7d);
