using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace common;

#pragma warning disable CA1716 // Identifiers should not match keywords
/// <summary>
/// Represents a type that may or may not contain a value.
/// </summary>
[JsonConverter(typeof(OptionJsonConverter))]
public sealed record Option<T>
#pragma warning restore CA1716 // Identifiers should not match keywords
{
    private readonly T? value;
    private readonly bool isSome;

    private Option()
    {
        isSome = false;
    }

    private Option(T value)
    {
        this.value = value;
        isSome = true;
    }

    /// <summary>
    /// Gets whether this option contains no value.
    /// </summary>
    public bool IsNone => !isSome;

    /// <summary>
    /// Gets whether this option contains a value.
    /// </summary>
    public bool IsSome => isSome;

#pragma warning disable CA1000 // Do not declare static members on generic types
    internal static Option<T> Some(T value) =>
        new(value);

    /// <summary>
    /// Creates an empty option.
    /// </summary>
    /// <returns>An option representing no value.</returns>
    public static Option<T> None() =>
        new();
#pragma warning restore CA1000 // Do not declare static members on generic types

    public override string ToString() =>
        IsSome ? $"Some({value})" : "None";

    public bool Equals(Option<T>? other) =>
        (this, other) switch
        {
            (_, null) => false,
            ({ IsSome: true }, { IsSome: true }) => EqualityComparer<T?>.Default.Equals(value, other.value),
            ({ IsNone: true }, { IsNone: true }) => true,
            _ => false
        };

    public override int GetHashCode() =>
        value is null
        ? 0
        : EqualityComparer<T?>.Default.GetHashCode(value);

    /// <summary>
    /// Pattern matches on the option state.
    /// </summary>
    /// <typeparam name="T2">The return type.</typeparam>
    /// <param name="some">Function executed if the option contains a value.</param>
    /// <param name="none">Function executed if the option is empty.</param>
    /// <returns>The result of the executed function.</returns>
    public T2 Match<T2>(Func<T, T2> some, Func<T2> none) =>
        IsSome ? some(value!) : none();

    /// <summary>
    /// Pattern matches on the option state for side effects.
    /// </summary>
    /// <param name="some">Action executed if the option contains a value.</param>
    /// <param name="none">Action executed if the option is empty.</param>
    public void Match(Action<T> some, Action none)
    {
        if (IsSome)
            some(value!);
        else
            none();
    }

    /// <summary>
    /// Implicitly converts a value to Some(value).
    /// </summary>
    public static implicit operator Option<T>(T value) =>
        Some(value);

    /// <summary>
    /// Implicitly converts None to an empty option.
    /// </summary>
    public static implicit operator Option<T>(None _) =>
        None();
}

/// <summary>
/// Represents the absence of a value in an option.
/// </summary>
public readonly record struct None
{
    public override string ToString() =>
        "None";

    public override int GetHashCode() => 0;
}

#pragma warning disable CA1716 // Identifiers should not match keywords
public static class Option
#pragma warning restore CA1716 // Identifiers should not match keywords
{
    /// <summary>
    /// Creates an option containing a value.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value to wrap.</param>
    /// <returns>Some(value).</returns>
    public static Option<T> Some<T>(T value) =>
        Option<T>.Some(value);

    /// <summary>
    /// A None value for creating empty options.
    /// </summary>
    public static None None { get; }

    /// <summary>
    /// Filters an option using a predicate.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to filter.</param>
    /// <param name="predicate">The predicate function.</param>
    /// <returns>The option if it satisfies the predicate, otherwise None.</returns>
    public static Option<T> Where<T>(this Option<T> option, Func<T, bool> predicate) =>
        option.Match(t => predicate(t) ? option : None,
                     () => None);

    /// <summary>
    /// Transforms the option value using a function.
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="option">The option to transform.</param>
    /// <param name="f">The transformation function.</param>
    /// <returns>Some(f(value)) if Some, otherwise None.</returns>
    public static Option<T2> Map<T, T2>(this Option<T> option, Func<T, T2> f) =>
        option.Match(t => Some(f(t)),
                     () => None);

    /// <summary>
    /// Asynchronously transforms the option value using a function that returns a ValueTask.
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="option">The option to transform.</param>
    /// <param name="f">The async transformation function.</param>
    /// <returns>Some(await f(value)) if Some, otherwise None.</returns>
    public static async ValueTask<Option<T2>> MapTask<T, T2>(this Option<T> option, Func<T, ValueTask<T2>> f) =>
        await option.Match(async t => Some(await f(t)),
                           async () => await ValueTask.FromResult(Option<T2>.None()));

    /// <summary>
    /// Chains option operations together (monadic bind).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="option">The option to bind.</param>
    /// <param name="f">The function that returns an option.</param>
    /// <returns>f(value) if Some, otherwise None.</returns>
    public static Option<T2> Bind<T, T2>(this Option<T> option, Func<T, Option<T2>> f) =>
        option.Match(t => f(t),
                     () => None);

    /// <summary>
    /// Asynchronously chains option operations together (monadic bind with async function).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="option">The option to bind.</param>
    /// <param name="f">The async function that returns an option.</param>
    /// <returns>await f(value) if Some, otherwise None.</returns>
    public static async ValueTask<Option<T2>> BindTask<T, T2>(this Option<T> option, Func<T, ValueTask<Option<T2>>> f) =>
        await option.Match(async t => await f(t),
                           async () => await ValueTask.FromResult(Option<T2>.None()));

    /// <summary>
    /// Projects the option value (LINQ support).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="option">The option to project.</param>
    /// <param name="f">The projection function.</param>
    /// <returns>The projected option.</returns>
    public static Option<T2> Select<T, T2>(this Option<T> option, Func<T, T2> f) =>
        option.Map(f);

    /// <summary>
    /// Projects and flattens nested options (LINQ support).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The intermediate value type.</typeparam>
    /// <typeparam name="TResult">The result value type.</typeparam>
    /// <param name="option">The source option.</param>
    /// <param name="f">The function that returns an intermediate option.</param>
    /// <param name="selector">The result selector function.</param>
    /// <returns>The flattened result option.</returns>
    public static Option<TResult> SelectMany<T, T2, TResult>(this Option<T> option, Func<T, Option<T2>> f,
                                                         Func<T, T2, TResult> selector) =>
        option.Bind(t => f(t).Map(t2 => selector(t, t2)));

    /// <summary>
    /// Provides a fallback value for empty options.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to check.</param>
    /// <param name="f">Function that provides the default value.</param>
    /// <returns>The option value if Some, otherwise the default value.</returns>
    public static T IfNone<T>(this Option<T> option, Func<T> f) =>
        option.Match(t => t,
                     f);

    /// <summary>
    /// Provides a fallback option for empty options.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to check.</param>
    /// <param name="f">Function that provides the fallback option.</param>
    /// <returns>The original option if Some, otherwise the fallback.</returns>
    public static Option<T> IfNone<T>(this Option<T> option, Func<Option<T>> f) =>
        option.Match(t => option,
                     f);

    /// <summary>
    /// Extracts the option value or throws an exception.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to check.</param>
    /// <param name="getException">Function that creates the exception to throw.</param>
    /// <returns>The option value.</returns>
    /// <exception cref="Exception">Thrown when the option is None.</exception>
    public static T IfNoneThrow<T>(this Option<T> option, Func<Exception> getException) =>
        option.Match(t => t,
                     () => throw getException());

    /// <summary>
    /// Converts the option to a nullable reference type.
    /// </summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="option">The option to convert.</param>
    /// <returns>The option value if Some, otherwise null.</returns>
    public static T? IfNoneNull<T>(this Option<T> option) where T : class =>
    option.Match(t => (T?)t,
                 () => null);

    /// <summary>
    /// Converts the option to a nullable value type.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to convert.</param>
    /// <returns>The option value if Some, otherwise null.</returns>
    public static T? IfNoneNullable<T>(this Option<T> option) where T : struct =>
        option.Match(t => (T?)t,
                     () => null);

    /// <summary>
    /// Executes an action if the option contains a value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to check.</param>
    /// <param name="f">The action to execute.</param>
    public static void Iter<T>(this Option<T> option, Action<T> f) =>
        option.Match(f,
                     () => { });

    /// <summary>
    /// Executes an async action if the option contains a value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="option">The option to check.</param>
    /// <param name="f">The async action to execute.</param>
    /// <returns>A task representing the async operation.</returns>
    public static async ValueTask IterTask<T>(this Option<T> option, Func<T, ValueTask> f) =>
        await option.Match<ValueTask>(async t => await f(t),
                                      () => ValueTask.CompletedTask);
}

/// <summary>
/// Represents the result of an operation that can either succeed with a value or fail with an error.
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
    /// Gets whether this result represents a success.
    /// </summary>
    public bool IsSuccess => isSuccess;

    /// <summary>
    /// Gets whether this result represents an error.
    /// </summary>
    public bool IsError => isSuccess is false;

#pragma warning disable CA1000 // Do not declare static members on generic types
    internal static Result<T> Success(T value) =>
        new(value);

    internal static Result<T> Error(Error error) =>
        new(error);
#pragma warning restore CA1000 // Do not declare static members on generic types

    /// <summary>
    /// Pattern matches on the result state.
    /// </summary>
    /// <typeparam name="TResult">The return type.</typeparam>
    /// <param name="onSuccess">Function executed if the result is successful.</param>
    /// <param name="onError">Function executed if the result is an error.</param>
    /// <returns>The result of the executed function.</returns>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onError) =>
        IsSuccess ? onSuccess(value!) : onError(error!);

    /// <summary>
    /// Pattern matches on the result state for side effects.
    /// </summary>
    /// <param name="onSuccess">Action executed if the result is successful.</param>
    /// <param name="onError">Action executed if the result is an error.</param>
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
    /// Implicitly converts a value to Success(value).
    /// </summary>
    public static implicit operator Result<T>(T value) =>
        Success(value);

    /// <summary>
    /// Implicitly converts an error to Error(error).
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
    /// Creates a successful result containing a value.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value to wrap.</param>
    /// <returns>Success(value).</returns>
    public static Result<T> Success<T>(T value) =>
        Result<T>.Success(value);

    /// <summary>
    /// Creates an error result containing an error.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="error">The error to wrap.</param>
    /// <returns>Error(error).</returns>
    public static Result<T> Error<T>(Error error) =>
        Result<T>.Error(error);

    /// <summary>
    /// Transforms the success value using a function.
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="result">The result to transform.</param>
    /// <param name="f">The transformation function.</param>
    /// <returns>Success(f(value)) if successful, otherwise the original error.</returns>
    public static Result<T2> Map<T, T2>(this Result<T> result, Func<T, T2> f) =>
        result.Match(value => Success(f(value)),
                     error => Error<T2>(error));

    /// <summary>
    /// Asynchronously transforms the success value using a function that returns a ValueTask.
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="result">The result to transform.</param>
    /// <param name="f">The async transformation function.</param>
    /// <returns>Success(await f(value)) if successful, otherwise the original error.</returns>
    public static async ValueTask<Result<T2>> MapTask<T, T2>(this Result<T> result, Func<T, ValueTask<T2>> f) =>
        await result.Match(async value => Success(await f(value)),
                           async error => await ValueTask.FromResult(Error<T2>(error)));

    /// <summary>
    /// Transforms the error, preserving any success value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to transform.</param>
    /// <param name="f">The error transformation function.</param>
    /// <returns>The original success, or Error(f(error)) if error.</returns>
    public static Result<T> MapError<T>(this Result<T> result, Func<Error, Error> f) =>
        result.Match(value => Success(value),
                     error => Error<T>(f(error)));

    /// <summary>
    /// Chains result operations together (monadic bind).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="result">The result to bind.</param>
    /// <param name="f">The function that returns a result.</param>
    /// <returns>f(value) if successful, otherwise the original error.</returns>
    public static Result<T2> Bind<T, T2>(this Result<T> result, Func<T, Result<T2>> f) =>
        result.Match(value => f(value),
                     error => Error<T2>(error));

    /// <summary>
    /// Asynchronously chains result operations together (monadic bind with async function).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="result">The result to bind.</param>
    /// <param name="f">The async function that returns a result.</param>
    /// <returns>await f(value) if successful, otherwise the original error.</returns>
    public static async ValueTask<Result<T2>> BindTask<T, T2>(this Result<T> result, Func<T, ValueTask<Result<T2>>> f) =>
        await result.Match(async value => await f(value),
                           async error => await ValueTask.FromResult(Error<T2>(error)));

    /// <summary>
    /// Projects the result value (LINQ support).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The result value type.</typeparam>
    /// <param name="result">The result to project.</param>
    /// <param name="f">The projection function.</param>
    /// <returns>The projected result.</returns>
    public static Result<T2> Select<T, T2>(this Result<T> result, Func<T, T2> f) =>
        result.Map(f);

    /// <summary>
    /// Projects and flattens nested results (LINQ support).
    /// </summary>
    /// <typeparam name="T">The source value type.</typeparam>
    /// <typeparam name="T2">The intermediate value type.</typeparam>
    /// <typeparam name="TResult">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="f">The function that returns an intermediate result.</param>
    /// <param name="selector">The result selector function.</param>
    /// <returns>The flattened result.</returns>
    public static Result<TResult> SelectMany<T, T2, TResult>(this Result<T> result, Func<T, Result<T2>> f,
                                                             Func<T, T2, TResult> selector) =>
        result.Bind(value => f(value)
              .Map(value2 => selector(value, value2)));

    /// <summary>
    /// Provides a fallback value for error results.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to check.</param>
    /// <param name="f">Function that provides the fallback value.</param>
    /// <returns>The success value if successful, otherwise the fallback value.</returns>
    public static T IfError<T>(this Result<T> result, Func<Error, T> f) =>
        result.Match(value => value,
                     f);

    /// <summary>
    /// Provides a fallback result for error results.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to check.</param>
    /// <param name="f">Function that provides the fallback result.</param>
    /// <returns>The original result if successful, otherwise the fallback.</returns>
    public static Result<T> IfError<T>(this Result<T> result, Func<Error, Result<T>> f) =>
        result.Match(_ => result,
                     f);

    /// <summary>
    /// Executes an action if the result is successful.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to check.</param>
    /// <param name="f">The action to execute.</param>
    public static void Iter<T>(this Result<T> result, Action<T> f) =>
        result.Match(f,
                     _ => { });

    /// <summary>
    /// Executes an async action if the result is successful.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to check.</param>
    /// <param name="f">The async action to execute.</param>
    /// <returns>A task representing the async operation.</returns>
    public static async ValueTask IterTask<T>(this Result<T> result, Func<T, ValueTask> f) =>
        await result.Match<ValueTask>(async value => await f(value),
                                      _ => ValueTask.CompletedTask);

    /// <summary>
    /// Extracts the success value or throws the error as an exception.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to check.</param>
    /// <returns>The success value.</returns>
    /// <exception cref="Exception">Thrown when the result is an error.</exception>
    public static T IfErrorThrow<T>(this Result<T> result) =>
        result.Match(value => value,
                     error => throw error.ToException());

    /// <summary>
    /// Converts the result to a nullable reference type.
    /// </summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="result">The result to convert.</param>
    /// <returns>The success value if successful, otherwise null.</returns>
    public static T? IfErrorNull<T>(this Result<T> result) where T : class =>
        result.Match(value => (T?)value,
                     _ => null);

    /// <summary>
    /// Converts the result to a nullable value type.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to convert.</param>
    /// <returns>The success value if successful, otherwise null.</returns>
    public static T? IfErrorNullable<T>(this Result<T> result) where T : struct =>
        result.Match(value => (T?)value,
                     _ => null);

    /// <summary>
    /// Converts a result to an option, discarding error information.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to convert.</param>
    /// <returns>Some(value) if the result is successful, otherwise None.</returns>
    public static Option<T> ToOption<T>(this Result<T> result) =>
        result.Match(Option.Some, _ => Option.None);
}

#pragma warning disable CA1716 // Identifiers should not match keywords
/// <summary>
/// Represents an error containing one or more messages.
/// </summary>
public record Error
#pragma warning restore CA1716 // Identifiers should not match keywords
{
    private readonly ImmutableHashSet<string> messages;

    protected Error(IEnumerable<string> messages)
    {
        this.messages = [.. messages];
    }

    /// <summary>
    /// Gets all error messages as an immutable set.
    /// </summary>
    public ImmutableHashSet<string> Messages => messages;

    /// <summary>
    /// Creates an error from one or more messages.
    /// </summary>
    /// <param name="messages">The error messages.</param>
    /// <returns>An error containing the specified messages.</returns>
    public static Error From(params string[] messages) =>
        new(messages);

    /// <summary>
    /// Creates an error from an exception.
    /// </summary>
    /// <param name="exception">The exception to wrap.</param>
    /// <returns>An exceptional error containing the exception.</returns>
    public static Error From(Exception exception) =>
        new Exceptional(exception);

    /// <summary>
    /// Converts the error to an appropriate exception.
    /// </summary>
    /// <returns>An exception representing this error.</returns>
    public virtual Exception ToException() =>
        messages.ToArray() switch
        {
            [var message] => new InvalidOperationException(message),
            _ => new AggregateException(messages.Select(message => new InvalidOperationException(message)))
        };

    public override string ToString() =>
        messages.ToArray() switch
        {
            [var message] => message,
            _ => string.Join("; ", messages)
        };

    /// <summary>
    /// Implicitly converts a string to an error.
    /// </summary>
    public static implicit operator Error(string message) =>
        From(message);

    /// <summary>
    /// Implicitly converts an exception to an error.
    /// </summary>
    public static implicit operator Error(Exception exception) =>
        From(exception);

    /// <summary>
    /// Combines two errors into a single error.
    /// </summary>
    /// <param name="left">The first error.</param>
    /// <param name="right">The second error.</param>
    /// <returns>An error containing messages from both errors.</returns>
    public static Error operator +(Error left, Error right) =>
        (left.messages, right.messages) switch
        {
            ({ Count: 0 }, _) => right,
            (_, { Count: 0 }) => left,
            _ => new(left.messages.Union(right.messages))
        };

    public virtual bool Equals(Error? other) =>
        (this, other) switch
        {
            (_, null) => false,
            ({ messages.Count: 0 }, { messages.Count: 0 }) => true,
            _ => messages.SetEquals(other.messages)
        };

    public override int GetHashCode() =>
        messages.Count switch
        {
            0 => 0,
            _ => messages.Aggregate(0, (hash, message) => HashCode.Combine(hash, message.GetHashCode()))
        };

    /// <summary>
    /// Represents an error that wraps an exception.
    /// </summary>
    public sealed record Exceptional : Error
    {
        internal Exceptional(Exception exception) : base([exception.Message])
        {
            Exception = exception;
        }

        /// <summary>
        /// Gets the wrapped exception.
        /// </summary>
        public Exception Exception { get; }

        /// <summary>
        /// Returns the original wrapped exception.
        /// </summary>
        /// <returns>The original exception.</returns>
        public override Exception ToException() => Exception;

        public bool Equals(Error.Exceptional? other) =>
            (this, other) switch
            {
                (_, null) => false,
                _ => Exception.Equals(other.Exception)
            };

        public override int GetHashCode() =>
            Exception.GetHashCode();
    }
}

/// <summary>
/// Represents the absence of a meaningful value. Used as a functional equivalent 
/// of void that can be used in generic contexts.
/// </summary>
public readonly record struct Unit
{
    public static Unit Instance { get; }

    public override string ToString() => "()";

    public override int GetHashCode() => 0;
}

public static class FunctionalExtensions
{


    /// <summary>
    /// Transforms an <see cref="Option{T}"/> into a <see cref="Result{Option{T2}}"/> by
    /// applying a function that may fail. This is useful for preserving the option structure
    /// while handling potential failures in the transformation.
    /// </summary>
    /// <param name="option">The option to traverse.</param>
    /// <param name="f">The function that transforms the value and may fail.</param>
    /// <returns>
    /// If <paramref name="option"/> contains a value and <paramref name="f"/> succeeds, returns successful result with a non-empty option.
    /// If <paramref name="option"/> is empty, returns a successful result with an empty option.
    /// If <paramref name="f"/> fails, returns an error result.
    /// </returns>
    public static Result<Option<T2>> Traverse<T, T2>(this Option<T> option, Func<T, Result<T2>> f) =>
        option.Match(t => f(t).Map(Option.Some),
                     () => Result.Success(Option<T2>.None()));
}