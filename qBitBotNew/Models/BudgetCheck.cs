namespace qBitBotNew.Models;

public sealed record BudgetCheck(bool Allowed, int Used, int Limit, DateTimeOffset? ResetAt);
