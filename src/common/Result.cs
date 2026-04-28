using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace common;

/// <summary>
/// Represents the result of an operation that can either succeed with a value of type <typeparamref name="T"/> or fail with an <see cref="common.Error"/>.
/// </summary>
public sealed record Result<T>
{
    private readonly T? value;
    private readonly Error? error;
    private readonly bool isSuccess;

    private Result(T value)
    {
        this.value = value;
        isSuccess = true;
    }

    private Result(Error error)
    {
        this.error = error;
        isSuccess = false;
    }

    /// <summary>
    /// True when the result is successful.
    /// </summary>
    public bool IsSuccess => isSuccess;

    /// <summary>
    /// True when the result is an error.
    /// </summary>
    public bool IsError => isSuccess is false;

#pragma warning disable CA1000 // Do not declare static members on generic types
    internal static Result<T> Success(T value) =>
        new(value);

    internal static Result<T> Error(Error error) =>
        new(error);
#pragma warning restore CA1000 // Do not declare static members on generic types

    /// <summary>
    /// Returns the result of <paramref name="onSuccess"/> or <paramref name="onError"/> based on the result's state.
    /// </summary>
    /// <param name="onSuccess">Executes when the result is successful.</param>
    /// <param name="onError">Executes when the result is an error.</param>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onError) =>
        IsSuccess ? onSuccess(value!) : onError(error!);

    /// <summary>
    /// Executes <paramref name="onSuccess"/> or <paramref name="onError"/> based on the result's state.
    /// </summary>
    /// <param name="onSuccess">Executes when the result is successful.</param>
    /// <param name="onError">Executes when the result is an error.</param>
    public void Match(Action<T> onSuccess, Action<Error> onError)
    {
        if (IsSuccess)
        {
            onSuccess(value!);
        }
        else
        {
            onError(error!);
        }
    }

    public override string ToString() =>
        IsSuccess ? $"Success: {value}" : $"Error: {error}";

    public bool Equals(Result<T>? other) =>
        (this, other) switch
        {
            (_, null) => false,
            ({ IsSuccess: true }, { IsSuccess: true }) =>
                EqualityComparer<T?>.Default.Equals(value, other.value),
            ({ IsError: true }, { IsError: true }) =>
                EqualityComparer<Error?>.Default.Equals(error, other.error),
            _ => false
        };

    public override int GetHashCode() =>
        HashCode.Combine(value, error);

    /// <summary>
    /// Converts a value to <c>Success(value)</c>.
    /// </summary>
    public static implicit operator Result<T>(T value) =>
        Success(value);

    /// <summary>
    /// Converts an <see cref="common.Error"/> to <c>Error(error)</c>.
    /// </summary>
    public static implicit operator Result<T>(Error error) =>
        Error(error);
}

/// <summary>
/// Provides static methods for creating and working with Result instances.
/// </summary>
public static class Result
{
    /// <summary>
    /// Wraps <paramref name="value"/> in a <see cref="Result{T}"/>.
    /// </summary>
    public static Result<T> Success<T>(T value) =>
        Result<T>.Success(value);

    /// <summary>
    /// Wraps <paramref name="error"/> in a <see cref="Result{T}"/>.
    /// </summary>
    public static Result<T> Error<T>(Error error) =>
        Result<T>.Error(error);

    /// <summary>
    /// Applies <paramref name="f"/> to the success value.
    /// </summary>
    /// <returns><c>Success(f(value))</c> if successful, otherwise the original error.</returns>
    public static Result<T2> Map<T, T2>(this Result<T> result, Func<T, T2> f) =>
        result.Match(value => Success(f(value)),
                     error => Error<T2>(error));

    /// <summary>
    /// Asynchronously applies <paramref name="f"/> to the success value.
    /// </summary>
    /// <returns><c>Success(await f(value))</c> if successful, otherwise the original error.</returns>
    public static async ValueTask<Result<T2>> MapTask<T, T2>(this Result<T> result, Func<T, ValueTask<T2>> f) =>
        await result.Match(async value => Success(await f(value)),
                           async error => await ValueTask.FromResult(Error<T2>(error)));

    /// <summary>
    /// Applies <paramref name="f"/> to the error, preserving any success value.
    /// </summary>
    /// <returns>The original success, or <c>Error(f(error))</c> if error.</returns>
    public static Result<T> MapError<T>(this Result<T> result, Func<Error, Error> f) =>
        result.Match(value => Success(value),
                     error => Error<T>(f(error)));

    /// <summary>
    /// Applies <paramref name="f"/> and flattens the result.
    /// </summary>
    /// <returns><c>f(value)</c> if successful, otherwise the original error.</returns>
    public static Result<T2> Bind<T, T2>(this Result<T> result, Func<T, Result<T2>> f) =>
        result.Match(value => f(value),
                     error => Error<T2>(error));

    /// <summary>
    /// Applies <paramref="f"/> and flattens the result.
    /// </summary>
    /// <returns><c>await f(value)</c> if successful, otherwise the original error.</returns>
    public static async ValueTask<Result<T2>> BindTask<T, T2>(this Result<T> result, Func<T, ValueTask<Result<T2>>> f) =>
        await result.Match(async value => await f(value),
                           async error => await ValueTask.FromResult(Error<T2>(error)));

    /// <summary>
    /// LINQ projection support. Enables syntax <c>from value in result select value</c>
    /// </summary>
    public static Result<T2> Select<T, T2>(this Result<T> result, Func<T, T2> f) =>
        result.Map(f);

    /// <summary>
    /// LINQ flattening support. Enables syntax <c>from x in result1 from y in result2 select x + y</c>
    /// </summary>
    public static Result<TResult> SelectMany<T, T2, TResult>(this Result<T> result, Func<T, Result<T2>> f,
                                                             Func<T, T2, TResult> selector) =>
        result.Bind(value => f(value)
              .Map(value2 => selector(value, value2)));

    /// <summary>
    /// Returns the success value if successful, otherwise the result of <paramref name="f"/>.
    /// </summary>
    public static T IfError<T>(this Result<T> result, Func<Error, T> f) =>
        result.Match(value => value,
                     f);

    /// <summary>
    /// Returns this result if successful, otherwise the result of <paramref name="f"/>.
    /// </summary>
    public static Result<T> IfError<T>(this Result<T> result, Func<Error, Result<T>> f) =>
        result.Match(_ => result,
                     f);

    /// <summary>
    /// Executes <paramref name="f"/> when the result is successful.
    /// </summary>
    public static void Iter<T>(this Result<T> result, Action<T> f) =>
        result.Match(f,
                     _ => { });

    /// <summary>
    /// Asynchronously executes <paramref name="f"/> when the result is successful.
    /// </summary>
    public static async ValueTask IterTask<T>(this Result<T> result, Func<T, ValueTask> f) =>
        await result.Match<ValueTask>(async value => await f(value),
                                      _ => ValueTask.CompletedTask);

    /// <summary>
    /// Returns the success value or throws the error as an exception.
    /// </summary>
    /// <exception cref="Exception">Thrown when the result is an error.</exception>
    public static T IfErrorThrow<T>(this Result<T> result) =>
        result.Match(value => value,
                     error => throw error.ToException());

    /// <summary>
    /// Converts the result to a nullable reference type.
    /// </summary>
    /// <returns>The success value if successful, otherwise <see langword="null"/>.</returns>
    public static T? IfErrorNull<T>(this Result<T> result) where T : class =>
        result.Match(value => (T?)value,
                     _ => null);

    /// <summary>
    /// Converts the result to a nullable value type.
    /// </summary>
    /// <returns>The success value if successful, otherwise <see langword="null"/>.</returns>
    public static T? IfErrorNullable<T>(this Result<T> result) where T : struct =>
        result.Match(value => (T?)value,
                     _ => null);

    /// <summary>
    /// Converts the result to an <see cref="Option{T}"/>, discarding error information.
    /// </summary>
    /// <returns><c>Some(value)</c> if successful, otherwise <see cref="Option.None"/>.</returns>
    public static Option<T> ToOption<T>(this Result<T> result) =>
        result.Match(Option.Some, _ => Option.None);

    /// <summary>
    /// Projects two results into a new result using <paramref name="f"/>, accumulating errors if any.
    /// </summary>
    public static Result<T3> Apply<T1, T2, T3>(this Result<T1> first, Result<T2> second, Func<T1, T2, T3> f) =>
        first.Match(value1 => second.Match(value2 => Success(f(value1, value2)),
                                           error2 => Error<T3>(error2)),
                    error1 => second.Match(_ => Error<T3>(error1),
                                           error2 => Error<T3>(error1 + error2)));
}