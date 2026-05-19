namespace qBitBotNew.Models;

public record Result<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    public static Result<T> Success(T value) => new() { Value = value };
    public static Result<T> Failure(string error) => new() { Error = error };

    public static implicit operator Result<T>(T value) => Success(value);
}

public record Result
{
    public string? Error { get; init; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    public static Result Success() => new();
    public static Result Failure(string error) => new() { Error = error };
}
