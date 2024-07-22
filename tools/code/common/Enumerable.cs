using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Nito.Comparers;
using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class IEnumerableExtensions
{
    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed sequentially. The function returns after all actions have executed.
    /// </summary>
    public static void Iter<T>(this IEnumerable<T> enumerable, Action<T> action)
    {
        foreach (var t in enumerable)
        {
            action(t);
        }
    }

    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed sequentially. The function returns after all actions have executed.
    /// </summary>
    public static async ValueTask Iter<T>(this IEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, _) => await action(t), maxDegreeOfParallelism: 1, cancellationToken);

    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed in parallel. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T>(this IEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
    await enumerable.IterParallel(async (t, _) => await action(t), maxDegreeOfParallelism: -1, cancellationToken);

    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed in parallel. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T>(this IEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(action, maxDegreeOfParallelism: -1, cancellationToken);

    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// <paramref name="maxDegreeOfParallelism"/> controls the maximum number of parallel actions. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T>(this IEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(enumerable, parallelOptions: options, action);
    }

    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed in parallel. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T1, T2>(this IEnumerable<(T1, T2)> enumerable, Func<T1, T2, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, cancellationToken) => await action(t.Item1, t.Item2, cancellationToken), cancellationToken);

    /// <summary>
    /// Applies <paramref name="f"/> to each element and filters out <seealso cref="Option.None"/> values.
    /// </summary>
    public static IEnumerable<T2> Choose<T, T2>(this IEnumerable<T> enumerable, Func<T, Option<T2>> f)
    {
        var enumerableM = enumerable.AsEnumerableM();

        return EnumerableMExtensions.Choose(enumerableM, f);
    }

    /// <summary>
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and returns the first Option of <typeparamref name="T2"/>
    /// that is Some. If all options are None, returns a None.
    /// </summary>
    public static async ValueTask<Option<T2>> Pick<T, T2>(this IEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask<Option<T2>>> f, CancellationToken cancellationToken)
    {
        foreach (var item in enumerable)
        {
            var option = await f(item, cancellationToken);

            if (option.IsSome)
            {
                return option;
            }
        }

        return Option<T2>.None;
    }

    /// <summary>
    /// Returns the first item in the enumerable. If the enumerable is empty, returns <seealso cref="Option.None"/>.
    /// </summary>
    public static Option<T> HeadOrNone<T>(this IEnumerable<T> enumerable)
    {
        var m = enumerable.AsEnumerableM();

        return FoldableExtensions.Head(m);
    }

    /// <summary>
    /// Returns the last item in the enumerable. If the enumerable is empty, returns <seealso cref="Option.None"/>.
    /// </summary>
    public static Option<T> LastOrNone<T>(this IEnumerable<T> enumerable)
    {
        var m = enumerable.AsEnumerableM();

        return FoldableExtensions.Last(m);
    }

    public static FrozenSet<T> ToFrozenSet<T, TKey>(this IEnumerable<T> enumerable, Func<T, TKey> keySelector)
    {
        var comparer = EqualityComparerBuilder.For<T>().EquateBy(keySelector);

        return enumerable.ToFrozenSet(comparer);
    }

    public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(this IEnumerable<(TKey, TValue)> enumerable, IEqualityComparer<TKey>? comparer = default) where TKey : notnull =>
        enumerable.ToFrozenDictionary(kvp => kvp.Item1, kvp => kvp.Item2, comparer);
}

public static class IAsyncEnumerableExtensions
{
    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed sequentially. The function returns after all actions have executed.
    /// </summary>
    public static async ValueTask Iter<T>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.Iter(async (t, cancellationToken) => await action(t), cancellationToken);

    /// <summary>
    /// Iterates over an <seealso cref="IEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed sequentially. The function returns after all actions have executed.
    /// </summary>
    public static async ValueTask Iter<T>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        await foreach (var item in enumerable.WithCancellation(cancellationToken))
        {
            await action(item, cancellationToken);
        }
    }

    /// <summary>
    /// Iterates over an <seealso cref="IAsyncEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed in parallel. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
    await enumerable.IterParallel(async (t, _) => await action(t), maxDegreeOfParallelism: -1, cancellationToken);

    /// <summary>
    /// Iterates over an <seealso cref="IAsyncEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed in parallel. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(action, maxDegreeOfParallelism: -1, cancellationToken);

    /// <summary>
    /// Iterates over an <seealso cref="IAsyncEnumerable{T}"/> and executes <paramref name="action"/> for each element.
    /// <paramref name="maxDegreeOfParallelism"/> controls the maximum number of parallel actions. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(enumerable, parallelOptions: options, action);
    }

    /// <summary>
    /// Iterates over an <seealso cref="IAsyncEnumerable"/> and executes <paramref name="action"/> for each element.
    /// Each action is executed in parallel. The function will wait for all actions to complete before returning.
    /// </summary>
    public static async ValueTask IterParallel<T1, T2>(this IAsyncEnumerable<(T1, T2)> enumerable, Func<T1, T2, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, cancellationToken) => await action(t.Item1, t.Item2, cancellationToken), cancellationToken);

    public static async ValueTask<FrozenSet<T>> ToFrozenSet<T>(this IAsyncEnumerable<T> enumerable, CancellationToken cancellationToken, IEqualityComparer<T>? comparer = default)
    {
        var items = await enumerable.ToListAsync(cancellationToken);

        return items.ToFrozenSet(comparer);
    }

    /// <summary>
    /// Applies <paramref name="f"/> to each element and filters out <seealso cref="Option.None"/> values.
    /// </summary>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, Option<T2>> f) =>
        enumerable.Choose(async (t, cancellationToken) => await ValueTask.FromResult(f(t)));

    /// <summary>
    /// Applies <paramref name="f"/> to each element and filters out <seealso cref="Option.None"/> values.
    /// </summary>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask<Option<T2>>> f) =>
        enumerable.Choose(async (t, cancellationToken) => await f(t));

    /// <summary>
    /// Applies <paramref name="f"/> to each element and filters out <seealso cref="Option.None"/> values.
    /// </summary>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask<Option<T2>>> f) =>
        enumerable.SelectAwaitWithCancellation(f)
                  .Where(option => option.IsSome)
                  .Select(option => option.ValueUnsafe()!);

    /// <summary>
    /// 
    /// </summary>
    public static async ValueTask<Option<T>> FirstOrNone<T>(this IAsyncEnumerable<T> enumerable, CancellationToken cancellationToken) =>
        await enumerable.Select(Option<T>.Some)
                        .DefaultIfEmpty(Option<T>.None)
                        .FirstAsync(cancellationToken);

    /// <summary>
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and returns the first Option of <typeparamref name="T2"/>
    /// that is Some. If all options are None, returns a None.
    /// </summary>
    public static async ValueTask<Option<T2>> Pick<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask<Option<T2>>> f, CancellationToken cancellationToken) =>
        await enumerable.Choose(f)
                        .FirstOrNone(cancellationToken);

    public static async ValueTask<FrozenDictionary<TKey, TValue>> ToFrozenDictionary<TKey, TValue>(this IAsyncEnumerable<(TKey, TValue)> enumerable, CancellationToken cancellationToken, IEqualityComparer<TKey>? comparer = default) where TKey : notnull
    {
        var array = await enumerable.ToArrayAsync(cancellationToken);

        return array.ToFrozenDictionary(comparer);
    }
}

public static class KeyValuePairExtensions
{
    /// <summary>
    /// Applies <paramref name="f"/> to each value and filters out <seealso cref="Option.None"/> values.
    /// </summary>
    public static IEnumerable<KeyValuePair<TKey, TValue2>> ChooseValues<TKey, TValue, TValue2>(this IEnumerable<KeyValuePair<TKey, TValue>> keyValuePairs, Func<TValue, Option<TValue2>> f) =>
        keyValuePairs.Choose(kvp => from value2 in f(kvp.Value)
                                    select KeyValuePair.Create(kvp.Key, value2));

    /// <summary>
    /// Applies <paramref name="f"/> to each key and filters out <seealso cref="Option.None"/> keys.
    /// </summary>
    public static IEnumerable<KeyValuePair<TKey2, TValue>> ChooseKeys<TKey, TKey2, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> keyValuePairs, Func<TKey, Option<TKey2>> f) =>
        keyValuePairs.Choose(kvp => from key2 in f(kvp.Key)
                                    select KeyValuePair.Create(key2, kvp.Value));

    /// <summary>
    /// Creates a new key value pair whose value is <paramref name="f"/>(<paramref name="keyValuePair"/>.Value).
    /// </summary>
    public static KeyValuePair<TKey, TValue2> MapValue<TKey, TValue, TValue2>(this KeyValuePair<TKey, TValue> keyValuePair, Func<TValue, TValue2> f) =>
        KeyValuePair.Create(keyValuePair.Key, f(keyValuePair.Value));

    /// <summary>
    /// Creates a new key value pair whose key is <paramref name="f"/>(<paramref name="keyValuePair"/>.Key).
    /// </summary>
    public static KeyValuePair<TKey2, TValue> MapKey<TKey, TKey2, TValue>(this KeyValuePair<TKey, TValue> keyValuePair, Func<TKey, TKey2> f) =>
        KeyValuePair.Create(f(keyValuePair.Key), keyValuePair.Value);

    /// <summary>
    /// Applies <paramref name="f"/> to each key
    /// </summary>
    public static IEnumerable<KeyValuePair<TKey2, TValue>> MapKey<TKey, TKey2, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> enumerable, Func<TKey, TKey2> f) =>
        enumerable.Select(kvp => kvp.MapKey(f));

    /// <summary>
    /// Applies <paramref name="f"/> to each value
    /// </summary>
    public static IEnumerable<KeyValuePair<TKey, TValue2>> MapValue<TKey, TValue, TValue2>(this IEnumerable<KeyValuePair<TKey, TValue>> enumerable, Func<TValue, TValue2> f) =>
        enumerable.Select(kvp => kvp.MapValue(f));

    /// <summary>
    /// Removes keys where the predicate is false
    /// </summary>
    public static IEnumerable<KeyValuePair<TKey, TValue>> WhereKey<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> enumerable, Func<TKey, bool> predicate) =>
        enumerable.Where(kvp => predicate(kvp.Key));

    /// <summary>
    /// Removes values where the predicate is false
    /// </summary>
    public static IEnumerable<KeyValuePair<TKey, TValue>> WhereValue<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> enumerable, Func<TValue, bool> predicate) =>
        enumerable.Where(kvp => predicate(kvp.Value));
}

public static class DictionaryExtensions
{
    public static Option<TValue> Find<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key) =>
        dictionary.TryGetValue(key, out var value)
            ? Option<TValue>.Some(value)
            : Option<TValue>.None;
}