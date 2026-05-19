using Microsoft.Extensions.Logging;

namespace qBitBotNew.Services;

public sealed partial class FeedbackService(ILogger<FeedbackService> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "ResponseFeedback — MessageId: {MessageId}, UserId: {UserId}, ChannelId: {ChannelId}, Helpful: {Helpful}")]
    public partial void LogFeedback(ulong messageId, ulong userId, ulong channelId, bool helpful);
}
