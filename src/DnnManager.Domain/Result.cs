namespace DnnManager.Domain;

/// <summary>Generic operation result. No exceptions across layer boundaries.</summary>
public sealed record Result(bool Success, string? Error = null)
{
    public static Result Ok() => new(true);
    public static Result Fail(string error) => new(false, error);

    /// <summary>The user said no to a question the operation asked: it didn't run, which isn't a failure to report.</summary>
    public static Result Aborted() => new(false, "Aborted by user.") { IsAborted = true };

    /// <summary>True for a result made by <see cref="Aborted"/>.</summary>
    public bool IsAborted { get; private init; }
}

public sealed record Result<T>(bool Success, T? Value, string? Error = null)
{
    public static Result<T> Ok(T value) => new(true, value);
    public static Result<T> Fail(string error) => new(false, default, error);

    /// <summary>Whether it worked, and why not - without the value.</summary>
    public Result WithoutValue() => new(Success, Error);
}
