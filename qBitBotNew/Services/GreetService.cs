using System.Collections.Concurrent;

namespace qBitBotNew.Services;

/// <summary>
/// Tracks new users (joined recently) whose message we may want to greet later, plus a
/// session-scoped set of users who have already been greeted. State is in-memory only —
/// a restart simply means pending greets are dropped (acceptable per phase 3 design).
/// </summary>
public sealed class GreetService
{
    public sealed record PendingGreet(
        ulong UserId,
        ulong ChannelId,
        ulong LastMessageId,
        DateTimeOffset LastSeenAt,
        ulong? GuildId);

    private readonly ConcurrentDictionary<ulong, PendingGreet> _pending = new();
    private readonly HashSet<ulong> _greeted = [];
    private readonly object _greetedLock = new();

    public bool IsAlreadyGreeted(ulong userId)
    {
        lock (_greetedLock)
            return _greeted.Contains(userId);
    }

    public void TrackOrUpdate(ulong userId, ulong channelId, ulong messageId, ulong? guildId)
    {
        if (IsAlreadyGreeted(userId))
            return;
        _pending[userId] = new PendingGreet(userId, channelId, messageId, DateTimeOffset.UtcNow, guildId);
    }

    /// <summary>Removes any pending greet for <paramref name="userId"/> — e.g. someone replied to them.</summary>
    public void Cancel(ulong userId) => _pending.TryRemove(userId, out _);

    public IReadOnlyCollection<PendingGreet> Snapshot() => _pending.Values.ToArray();

    /// <summary>
    /// Atomically marks the user as greeted and removes them from pending. Returns false if
    /// someone else (rare race) already greeted them.
    /// </summary>
    public bool MarkGreeted(ulong userId)
    {
        lock (_greetedLock)
        {
            if (!_greeted.Add(userId))
                return false;
        }
        _pending.TryRemove(userId, out _);
        return true;
    }
}
