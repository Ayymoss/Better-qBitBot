using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using qBitBotNew.Config;

namespace qBitBotNew.Services;

public sealed class RateLimiterService(IOptions<BotConfig> config)
{
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastInvocation = new();

    public bool IsRateLimited(ulong userId)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(config.Value.CooldownSeconds);

        if (_lastInvocation.TryGetValue(userId, out var lastTime) && now - lastTime < cooldown)
            return true;

        _lastInvocation[userId] = now;
        return false;
    }
}
