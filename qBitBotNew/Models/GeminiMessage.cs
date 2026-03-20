namespace qBitBotNew.Models;

/// <summary>
/// A single turn in a Gemini conversation. Role is "user" or "model".
/// </summary>
public sealed record GeminiMessage(string Role, string Content);
