using System;

namespace PoPunkouterSoftware.Domain.Azure;

/// <summary>
/// Represents the result of an operation, with success or error information.
/// Replaces null-returning patterns to surface errors to callers.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly record struct Result<T>(bool IsSuccess, T? Value, string? Error, Exception? Exception)
{
    /// <summary>Creates a successful result with the given value.</summary>
    public static Result<T> Success(T value) => new(IsSuccess: true, Value: value, Error: null, Exception: null);

    /// <summary>Creates a failed result with the given error message and optional exception.</summary>
    public static Result<T> Failure(string error, Exception? ex = null) =>
        new(IsSuccess: false, Value: default, Error: error, Exception: ex);
}
