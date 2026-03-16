using Microsoft.Extensions.Logging;

namespace qBitBotNew.Services;

public sealed class FeedbackService(ILogger<FeedbackService> logger)
{
    /// <summary>
    /// Logs user feedback on a bot response as a structured event for later analysis.
    /// </summary>
    public void LogFeedback(ulong messageId, ulong userId, ulong channelId, bool helpful)
    {
        logger.LogInformation(
            "ResponseFeedback — MessageId: {MessageId}, UserId: {UserId}, ChannelId: {ChannelId}, Helpful: {Helpful}",
            messageId, userId, channelId, helpful);
    }
}
