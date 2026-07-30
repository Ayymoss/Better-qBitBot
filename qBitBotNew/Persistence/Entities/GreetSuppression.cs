namespace qBitBotNew.Persistence.Entities;

/// <summary>Why a user will never receive the opt-in greet offer again.</summary>
public enum GreetSuppressionReason
{
    /// <summary>They've already been offered the greet once.</summary>
    Greeted,

    /// <summary>Another human replied to or @mentioned them — the humans have it covered.</summary>
    HumanEngaged,

    /// <summary>They (or someone on their behalf) invoked the bot directly.</summary>
    Invoked
}

/// <summary>
/// One row per user who must never be greeted again. Persisted so a restart can't resurrect
/// the offer — the greet is a one-shot, permanent decision per user.
/// </summary>
public sealed class GreetSuppression
{
    // Discord snowflake stored as signed long (see SnowflakeExtensions). Primary key —
    // one row per user, insert-once.
    public long UserId { get; set; }

    public GreetSuppressionReason Reason { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
