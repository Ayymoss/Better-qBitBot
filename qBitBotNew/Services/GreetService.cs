using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using qBitBotNew.Persistence;
using qBitBotNew.Persistence.Entities;

namespace qBitBotNew.Services;

/// <summary>
/// Tracks new users (joined recently) whose message we may want to greet later, and enforces
/// the one-shot rule: a user is offered the greet at most once, ever. Suppression is persisted
/// in the GreetSuppressions table so a restart can't resurrect an offer we already made — the
/// pending queue itself stays in memory (dropping it on restart just means no greet).
///
/// Suppression is permanent and applies to every future message from that user. Manual
/// invocation paths (@mention, reply, slash command, right-click) are unaffected.
/// </summary>
public sealed partial class GreetService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<GreetService> logger)
{
    public sealed record PendingGreet(
        ulong UserId,
        ulong ChannelId,
        ulong LastMessageId,
        DateTimeOffset LastSeenAt);

    private readonly ConcurrentDictionary<ulong, PendingGreet> _pending = new();

    // Mirror of the GreetSuppressions table. Every message hits IsSuppressedAsync, so the
    // cache keeps that off SQLite; safe to cache negatives too because this process is the
    // only writer.
    private readonly ConcurrentDictionary<ulong, bool> _suppressed = new();

    // Serialises inserts so two concurrent messages from the same user can't race into a
    // duplicate-key failure, and so TryClaimGreetAsync is a genuine check-and-set.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// True when this user must not be greeted. Fails closed: a DB error reports suppressed,
    /// because an un-sent greet is cheaper than a spammy duplicate one.
    /// </summary>
    public async ValueTask<bool> IsSuppressedAsync(ulong userId, CancellationToken ct = default)
    {
        if (_suppressed.TryGetValue(userId, out var cached))
            return cached;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var id = userId.ToDbId();
            var exists = await db.GreetSuppressions.AnyAsync(g => g.UserId == id, ct);
            _suppressed[userId] = exists;
            return exists;
        }
        catch (Exception ex)
        {
            LogSuppressionLookupFailed(ex, userId);
            return true;
        }
    }

    /// <summary>
    /// Permanently bars <paramref name="userId"/> from future greets and drops any pending
    /// entry. Idempotent.
    /// </summary>
    public async Task SuppressAsync(ulong userId, GreetSuppressionReason reason, CancellationToken ct = default)
    {
        _pending.TryRemove(userId, out _);

        if (_suppressed.TryGetValue(userId, out var known) && known)
            return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await InsertSuppressionAsync(userId, reason, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void TrackOrUpdate(ulong userId, ulong channelId, ulong messageId) =>
        _pending[userId] = new PendingGreet(userId, channelId, messageId, DateTimeOffset.UtcNow);

    public IReadOnlyCollection<PendingGreet> Snapshot() => _pending.Values.ToArray();

    /// <summary>
    /// Check-and-set for the worker: marks the user greeted and returns true only if nothing
    /// had already suppressed them. Returns false when the greet must not be sent.
    /// </summary>
    public async Task<bool> TryClaimGreetAsync(ulong userId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (await IsSuppressedAsync(userId, ct))
            {
                _pending.TryRemove(userId, out _);
                return false;
            }

            var inserted = await InsertSuppressionAsync(userId, GreetSuppressionReason.Greeted, ct);
            _pending.TryRemove(userId, out _);
            return inserted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Caller must hold _writeLock. Returns false if the row already existed or the write failed
    // — either way the in-memory cache is marked suppressed, so we err toward not greeting.
    private async Task<bool> InsertSuppressionAsync(ulong userId, GreetSuppressionReason reason, CancellationToken ct)
    {
        var id = userId.ToDbId();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.GreetSuppressions.AnyAsync(g => g.UserId == id, ct))
            {
                _suppressed[userId] = true;
                return false;
            }

            db.GreetSuppressions.Add(new GreetSuppression
            {
                UserId = id,
                Reason = reason,
                RecordedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            _suppressed[userId] = true;
            LogSuppressed(userId, reason);
            return true;
        }
        catch (Exception ex)
        {
            LogSuppressionWriteFailed(ex, userId);
            _suppressed[userId] = true;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Greet suppression lookup failed for user {UserId} — treating as suppressed")]
    private partial void LogSuppressionLookupFailed(Exception ex, ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist greet suppression for user {UserId} — suppressed in memory only")]
    private partial void LogSuppressionWriteFailed(Exception ex, ulong userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Greet suppressed for user {UserId} ({Reason})")]
    private partial void LogSuppressed(ulong userId, GreetSuppressionReason reason);
}
