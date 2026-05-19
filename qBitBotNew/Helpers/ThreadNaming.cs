using NetCord.Rest;

namespace qBitBotNew.Helpers;

internal static class ThreadNaming
{
    private const int MaxDiscordThreadName = 100;
    private const int SoftLimit = 80;
    private const string Fallback = "qBitBot question";

    public static string Build(string raw)
    {
        var trimmed = (raw ?? string.Empty).Replace('\n', ' ').Trim().Trim('"', '\'', '“', '”', '.', '!', '?');
        if (string.IsNullOrEmpty(trimmed))
            return Fallback;
        return trimmed.Length <= SoftLimit ? trimmed : trimmed[..(SoftLimit - 3)] + "...";
    }

    // Best topic available — Gemini's `topic` field when present, else first line of the question.
    public static string Pick(string? geminiTopic, string fallbackQuestion) =>
        Build(string.IsNullOrWhiteSpace(geminiTopic) ? fallbackQuestion : geminiTopic);

    public static async Task TryRenameAsync(RestClient client, ulong threadId, string newName, CancellationToken ct = default)
    {
        var safe = newName.Length > MaxDiscordThreadName ? newName[..MaxDiscordThreadName] : newName;
        try
        {
            await client.ModifyGuildChannelAsync(threadId, o => o.Name = safe, null, ct);
        }
        catch
        {
            // Rename is cosmetic — never let it break the response flow.
        }
    }
}
