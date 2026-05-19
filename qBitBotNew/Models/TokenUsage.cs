namespace qBitBotNew.Models;

public sealed record TokenUsage(int Prompt, int Cached, int Output, int Thoughts, int Total)
{
    public static readonly TokenUsage Empty = new(0, 0, 0, 0, 0);
}
