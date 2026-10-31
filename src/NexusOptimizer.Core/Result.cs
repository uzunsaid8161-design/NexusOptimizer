namespace NexusOptimizer.Core;

/// <summary>Ergebnis statt Exception für erwartbare Fehlerfälle.</summary>
public readonly record struct Result<T>
{
    private Result(T? value, string? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string error) => new(default, error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<string, TOut> onError) =>
        IsSuccess ? onSuccess(Value!) : onError(Error!);
}

public readonly record struct Result
{
    private Result(string? error) => Error = error;

    public string? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result Ok() => new(null);
    public static Result Fail(string error) => new(error);
}
