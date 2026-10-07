namespace qBitBotNew.Models;

/// <summary>
/// A single turn in a model conversation. Role is "user" or "assistant".
/// </summary>
public sealed record ChatMessage(string Role, string Content);
