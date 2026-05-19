using Microsoft.Extensions.Logging;

namespace qBitBotNew.Services;

public sealed partial class FeedbackService(ILogger<FeedbackService> logger)
{
    /// <summary>
    /// Logs user feedback on a bot response as a structured event for later analysis.
    /// </summary>
    public void LogFeedback(ulong messageId, ulong userId, ulong channelId, bool helpful)
    {
        LogResponseFeedback(messageId, userId, channelId, helpful);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ResponseFeedback — MessageId: {MessageId}, UserId: {UserId}, ChannelId: {ChannelId}, Helpful: {Helpful}")]
    private partial void LogResponseFeedback(ulong messageId, ulong userId, ulong channelId, bool helpful);
}
