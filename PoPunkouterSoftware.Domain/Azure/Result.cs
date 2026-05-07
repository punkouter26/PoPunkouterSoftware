using System;

namespace PoPunkouterSoftware.Domain.Azure;

/// <summary>
/// Represents the result of an operation, with success or error information.
/// Replaces null-returning patterns to surface errors to callers.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public class Result<T>
{
    public bool IsSuccess { get; private set; }
    public T? Value { get; private set; }
    public string? Error { get; private set; }
    public Exception? Exception { get; private set; }

    protected Result(T value)
    {
        IsSuccess = true;
        Value = value;
        Error = null;
        Exception = null;
    }

    protected Result(string error, Exception? ex = null)
    {
        IsSuccess = false;
        Value = default;
        Error = error;
        Exception = ex;
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(string error, Exception? ex = null) => new(error, ex);
}
