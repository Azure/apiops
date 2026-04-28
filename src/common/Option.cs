using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace common;

#pragma warning disable CA1716 // Identifiers should not match keywords
/// <summary>
/// Represents a type that may or may not contain a value of type <typeparamref name="T"/>.
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
    /// True when the option is empty.
    /// </summary>
    public bool IsNone => !isSome;

    /// <summary>
    /// True when the option contains a value.
    /// </summary>
    public bool IsSome => isSome;

#pragma warning disable CA1000 // Do not declare static members on generic types
    internal static Option<T> Some(T value) =>
        new(value);

    /// <summary>
    /// Creates an empty option.
    /// </summary>
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
    /// Returns the result of <paramref name="some"/> or <paramref name="none"/> based on the option's state.
    /// </summary>
    /// <param name="some">Executes when the option is Some.</param>
    /// <param name="none">Executes when the option is None.</param>
    public T2 Match<T2>(Func<T, T2> some, Func<T2> none) =>
        IsSome ? some(value!) : none();

    /// <summary>
    /// Executes <paramref name="some"/> or <paramref name="none"/> based on the option's state.
    /// </summary>
    /// <param name="some">Executes when the option is Some.</param>
    /// <param name="none">Executes when the option is None.</param>
    public void Match(Action<T> some, Action none)
    {
        if (IsSome)
            some(value!);
        else
            none();
    }

    /// <summary>
    /// Converts a value to <c>Some(value)</c>.
    /// </summary>
    public static implicit operator Option<T>(T value) =>
        Some(value);

    /// <summary>
    /// Converts <see cref="common.None"/> to an empty option.
    /// </summary>
    public static implicit operator Option<T>(None _) =>
        None();
}

/// <summary>
/// Sentinel type for empty options.
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
    /// Wraps <paramref name="value"/> in an <see cref="Option{T}"/>.
    /// </summary>
    public static Option<T> Some<T>(T value) =>
        Option<T>.Some(value);

    /// <summary>
    /// Singleton for creating empty options.
    /// </summary>
    public static None None { get; }

    /// <summary>
    /// Returns the option if <paramref name="predicate"/> succeeds, otherwise <see cref="common.None"/>.
    /// </summary>
    public static Option<T> Where<T>(this Option<T> option, Func<T, bool> predicate) =>
        option.Match(t => predicate(t) ? option : None,
                     () => None);

    /// <summary>
    /// Applies <paramref name="f"/> to the wrapped value.
    /// </summary>
    /// <returns><c>Some(f(value))</c> if Some, otherwise <see cref="common.None"/>.</returns>
    public static Option<T2> Map<T, T2>(this Option<T> option, Func<T, T2> f) =>
        option.Match(t => Some(f(t)),
                     () => None);

    /// <summary>
    /// Asynchronously applies <paramref name="f"/> to the wrapped value.
    /// </summary>
    /// <returns><c>Some(await f(value))</c> if Some, otherwise <see cref="common.None"/>.</returns>
    public static async ValueTask<Option<T2>> MapTask<T, T2>(this Option<T> option, Func<T, ValueTask<T2>> f) =>
        await option.Match(async t => Some(await f(t)),
                           async () => await ValueTask.FromResult(Option<T2>.None()));

    /// <summary>
    /// Applies <paramref name="f"/> and flattens the result.
    /// </summary>
    /// <returns><c>f(value)</c> if Some, otherwise <see cref="common.None"/>.</returns>
    public static Option<T2> Bind<T, T2>(this Option<T> option, Func<T, Option<T2>> f) =>
        option.Match(t => f(t),
                     () => None);

    /// <summary>
    /// Applies <paramref name="f"/> and flattens the result.
    /// </summary>
    /// <returns><c>await f(value)</c> if Some, otherwise <see cref="common.None"/>.</returns>
    public static async ValueTask<Option<T2>> BindTask<T, T2>(this Option<T> option, Func<T, ValueTask<Option<T2>>> f) =>
        await option.Match(async t => await f(t),
                           async () => await ValueTask.FromResult(Option<T2>.None()));

    /// <summary>
    /// LINQ projection support. Enables syntax <c>from value in option select value</c>
    /// </summary>
    public static Option<T2> Select<T, T2>(this Option<T> option, Func<T, T2> f) =>
        option.Map(f);

    /// <summary>
    /// LINQ flattening support. Enables syntax <c>from x in option1 from y in option2 select x + y</c>
    /// </summary>
    public static Option<TResult> SelectMany<T, T2, TResult>(this Option<T> option, Func<T, Option<T2>> f,
                                                         Func<T, T2, TResult> selector) =>
        option.Bind(t => f(t).Map(t2 => selector(t, t2)));

    /// <summary>
    /// Returns the wrapped value if Some, otherwise the result of <paramref name="f"/>.
    /// </summary>
    public static T IfNone<T>(this Option<T> option, Func<T> f) =>
        option.Match(t => t,
                     f);

    /// <summary>
    /// Returns this option if Some, otherwise the result of <paramref name="f"/>.
    /// </summary>
    public static Option<T> IfNone<T>(this Option<T> option, Func<Option<T>> f) =>
        option.Match(t => option,
                     f);

    /// <summary>
    /// Returns the wrapped value if Some, otherwise throws the result of <paramref name="getException"/>.
    /// </summary>
    /// <exception cref="Exception">Thrown when the option is <see cref="common.None"/>.</exception>
    public static T IfNoneThrow<T>(this Option<T> option, Func<Exception> getException) =>
        option.Match(t => t,
                     () => throw getException());

    /// <summary>
    /// Converts the option to a nullable reference type.
    /// </summary>
    /// <returns>The wrapped value if Some, otherwise <see langword="null"/>.</returns>
    public static T? IfNoneNull<T>(this Option<T> option) where T : class =>
    option.Match(t => (T?)t,
                 () => null);

    /// <summary>
    /// Converts the option to a nullable value type.
    /// </summary>
    /// <returns>The wrapped value if Some, otherwise <see langword="null"/>.</returns>
    public static T? IfNoneNullable<T>(this Option<T> option) where T : struct =>
        option.Match(t => (T?)t,
                     () => null);

    /// <summary>
    /// Executes <paramref name="f"/> when the option is Some.
    /// </summary>
    public static void Iter<T>(this Option<T> option, Action<T> f) =>
        option.Match(f,
                     () => { });

    /// <summary>
    /// Asynchronously executes <paramref name="f"/> when the option is Some.
    /// </summary>
    public static async ValueTask IterTask<T>(this Option<T> option, Func<T, ValueTask> f) =>
        await option.Match<ValueTask>(async t => await f(t),
                                      () => ValueTask.CompletedTask);

    /// <summary>
    /// Executes <paramref name="action"/> when the option is Some, then returns the original option.
    /// </summary>
    public static Option<T> Tap<T>(this Option<T> option, Action<T> action)
    {
        option.Iter(action);
        return option;
    }

    /// <summary>
    /// Converts the option to a result. If the option is None, using <paramref name="errorFactory"/> to produce an error.
    /// </summary>
    public static Result<T> ToResult<T>(this Option<T> option, Func<Error> errorFactory) =>
        option.Match(t => Result.Success(t),
                     () => errorFactory());
}
