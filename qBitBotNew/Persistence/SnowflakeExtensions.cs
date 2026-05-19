namespace qBitBotNew.Persistence;

/// <summary>
/// EF Core's SQLite provider can't translate LINQ expressions involving ulong (Discord
/// snowflake type). Entity columns are stored as long; these extensions hide the cast
/// at the persistence boundary so call sites don't sprinkle `unchecked((long)x)` everywhere.
/// Discord snowflakes fit comfortably in 63 bits — bit pattern survives intact.
/// </summary>
internal static class SnowflakeExtensions
{
    public static long ToDbId(this ulong id) => unchecked((long)id);

    public static long? ToDbId(this ulong? id) => id.HasValue ? unchecked((long)id.Value) : null;
}
