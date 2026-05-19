using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using qBitBotNew.Config;
using qBitBotNew.Persistence;

namespace qBitBotNew.Services;

/// <summary>
/// Polls the GreetService once a minute and fires the opt-in greet message when a pending
/// new-user entry has aged past GreetWaitMinutes. Suppresses the greet if the user already
/// has any feedback rows (they've used the bot before) or if a race already greeted them.
/// </summary>
public sealed partial class GreetWorker(
    GreetService greetService,
    RestClient restClient,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<BotConfig> botConfig,
    ILogger<GreetWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!botConfig.Value.GreetEnabled)
        {
            LogGreetDisabled();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogTickFailed(ex);
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromMinutes(botConfig.Value.GreetWaitMinutes);
        var now = DateTimeOffset.UtcNow;

        foreach (var pending in greetService.Snapshot())
        {
            if (now - pending.LastSeenAt < wait)
                continue;

            // Has the user ever interacted with the bot already? If so, don't greet —
            // we only want to onboard genuinely new arrivals once.
            var userIdLong = pending.UserId.ToDbId();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var hasFeedback = await db.Feedback.AnyAsync(f => f.UserId == userIdLong, ct);
            if (hasFeedback)
            {
                greetService.MarkGreeted(pending.UserId);
                continue;
            }

            if (!greetService.MarkGreeted(pending.UserId))
                continue;

            try
            {
                await SendGreetAsync(pending, ct);
            }
            catch (Exception ex)
            {
                LogGreetSendFailed(ex, pending.UserId, pending.ChannelId);
            }
        }
    }

    private async Task SendGreetAsync(GreetService.PendingGreet pending, CancellationToken ct)
    {
        var customId = $"greet_invoke:{pending.UserId}:{pending.ChannelId}:{pending.LastMessageId}";

        var props = new MessageProperties
        {
            Content = $"<@{pending.UserId}>",
            Embeds = [new EmbedProperties
            {
                Description =
                    "Hi! I'm **qBitBot**, a Gemini-powered assistant for qBitTorrent client questions. "
                  + "Want me to take a look at what you posted above and have a go at answering?",
                Color = new Color(67, 160, 71)
            }],
            Components = [new ActionRowProperties([
                new ButtonProperties(customId, "Ask qBitBot for me", ButtonStyle.Primary)
            ])],
            MessageReference = MessageReferenceProperties.Reply(pending.LastMessageId)
        };

        await restClient.SendMessageAsync(pending.ChannelId, props, null, ct);
        LogGreetSent(pending.UserId, pending.ChannelId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GreetWorker — feature disabled via config; worker exiting")]
    private partial void LogGreetDisabled();

    [LoggerMessage(Level = LogLevel.Warning, Message = "GreetWorker tick failed")]
    private partial void LogTickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send greet for user {UserId} in channel {ChannelId}")]
    private partial void LogGreetSendFailed(Exception ex, ulong userId, ulong channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "GreetWorker sent greet to user {UserId} in channel {ChannelId}")]
    private partial void LogGreetSent(ulong userId, ulong channelId);
}
