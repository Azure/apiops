using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Nito.Comparers;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class IEnumerableExtensions
{
    public static async ValueTask Iter<T>(this IEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, _) => await action(t), maxDegreeOfParallelism: 1, cancellationToken);

    public static async ValueTask IterParallel<T>(this IEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, _) => await action(t), maxDegreeOfParallelism: -1, cancellationToken);

    public static async ValueTask IterParallel<T>(this IEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(action, maxDegreeOfParallelism: -1, cancellationToken);

    public static async ValueTask IterParallel<T>(this IEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(enumerable, parallelOptions: options, action);
    }

    public static IAsyncEnumerable<T2> Choose<T, T2>(this IEnumerable<T> enumerable, Func<T, ValueTask<Option<T2>>> f) =>
        enumerable.ToAsyncEnumerable()
                  .Choose(f);

    /// <summary>
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and returns the first Option of <typeparamref name="T2"/>
    /// that is Some. If all options are None, returns a None.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="f"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static Option<T2> Pick<T, T2>(this IEnumerable<T> enumerable, Func<T, Option<T2>> f) =>
        enumerable.Select(f)
                  .Where(option => option.IsSome)
                  .DefaultIfEmpty(Option<T2>.None)
                  .First();

    public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(this IEnumerable<(TKey Key, TValue Value)> enumerable, IEqualityComparer<TKey>? comparer = default) where TKey : notnull =>
        enumerable.ToFrozenDictionary(x => x.Key, x => x.Value, comparer);

    public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue, TComparison>(this IEnumerable<(TKey Key, TValue Value)> enumerable, Func<TKey, TComparison> comparer) where TKey : notnull =>
        enumerable.ToFrozenDictionary(x => x.Key, x => x.Value, EqualityComparerBuilder.For<TKey>().EquateBy(comparer));

    public static FrozenSet<T> ToFrozenSet<T, TKey>(this IEnumerable<T> enumerable, Func<T, TKey> keySelector) =>
        enumerable.ToFrozenSet(EqualityComparerBuilder.For<T>().EquateBy(keySelector));
}

public static class IAsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> Do<T>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask> action) =>
        enumerable.SelectAwait(async t =>
        {
            await action(t);
            return t;
        });

    public static IAsyncEnumerable<T> Do<T>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action) =>
        enumerable.SelectAwaitWithCancellation(async (t, cancellationToken) =>
        {
            await action(t, cancellationToken);
            return t;
        });

    public static async ValueTask Iter<T>(this IAsyncEnumerable<T> enumerable, Action<T> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, _) =>
        {
            action(t);
            await ValueTask.CompletedTask;
        }, maxDegreeOfParallelism: 1, cancellationToken);

    public static async ValueTask Iter<T>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.Iter(async (t, _) => await action(t), cancellationToken);

    public static async ValueTask Iter<T>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(action, maxDegreeOfParallelism: 1, cancellationToken);

    public static async ValueTask IterParallel<T>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(async (t, _) => await action(t), maxDegreeOfParallelism: -1, cancellationToken);

    public static async ValueTask IterParallel<T>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask> action, CancellationToken cancellationToken) =>
        await enumerable.IterParallel(action, maxDegreeOfParallelism: -1, cancellationToken);

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
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and filters out cases where the resulting Option of <typeparamref name="T2"/> is None.
    /// </summary>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, Option<T2>> f) =>
        enumerable.Choose(async t => await f(t).AsValueTask());

    /// <summary>
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and filters out cases where the resulting Option of <typeparamref name="T2"/> is None.
    /// </summary>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask<Option<T2>>> f) =>
        enumerable.Choose((t, cancellationToken) => f(t));

    /// <summary>
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and filters out cases where the resulting Option of <typeparamref name="T2"/> is None.
    /// </summary>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, CancellationToken, ValueTask<Option<T2>>> f) =>
        enumerable.SelectAwaitWithCancellation(f)
                  .Where(option => option.IsSome)
                  .Select(option => option.ValueUnsafe());

    public static async ValueTask<Option<T>> HeadOrNone<T>(this IAsyncEnumerable<T> enumerable, CancellationToken cancellationToken) =>
        await enumerable.Select(Option<T>.Some)
                        .DefaultIfEmpty(Option<T>.None)
                        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Applies <paramref name="f"/> to each element of <paramref name="enumerable"/> and returns the first Option of <typeparamref name="T2"/>
    /// that is Some. If all options are None, returns a None.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="f"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async ValueTask<Option<T2>> Pick<T, T2>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask<Option<T2>>> f, CancellationToken cancellationToken) =>
        await enumerable.SelectAwait(f)
                        .Where(option => option.IsSome)
                        .DefaultIfEmpty(Option<T2>.None)
                        .FirstAsync(cancellationToken);

    public static async IAsyncEnumerable<TResult> FullJoin<T1, T2, TKey, TResult>(this IAsyncEnumerable<T1> first,
                                                                                  IAsyncEnumerable<T2> second,
                                                                                  Func<T1, ValueTask<TKey>> firstKeySelector,
                                                                                  Func<T2, ValueTask<TKey>> secondKeySelector,
                                                                                  Func<T1, ValueTask<TResult>> firstResultSelector,
                                                                                  Func<T2, ValueTask<TResult>> secondResultSelector,
                                                                                  Func<T1, T2, ValueTask<TResult>> bothResultSelector,
                                                                                  [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var secondLookup = await second.ToLookupAwaitAsync(secondKeySelector, cancellationToken);
        var keys = new System.Collections.Generic.HashSet<TKey>();

        await foreach (var firstItem in first.WithCancellation(cancellationToken))
        {
            var firstKey = await firstKeySelector(firstItem);
            keys.Add(firstKey);

            if (secondLookup.Contains(firstKey))
            {
                var secondItems = secondLookup[firstKey];

                foreach (var secondItem in secondItems)
                {
                    yield return await bothResultSelector(firstItem, secondItem);
                }
            }
            else
            {
                yield return await firstResultSelector(firstItem);
            }
        }

        foreach (var group in secondLookup)
        {
            if (keys.Contains(group.Key) is false)
            {
                foreach (var secondItem in group)
                {
                    yield return await secondResultSelector(secondItem);
                }
            }
        }
    }

    public static async ValueTask<FrozenSet<T>> ToFrozenSet<T>(this IAsyncEnumerable<T> enumerable, CancellationToken cancellationToken, IEqualityComparer<T>? comparer = null)
    {
        var result = await enumerable.ToArrayAsync(cancellationToken);

        return result.ToFrozenSet(comparer);
    }

    public static async ValueTask<FrozenDictionary<TKey, TValue>> ToFrozenDictionary<TKey, TValue>(this IAsyncEnumerable<(TKey Key, TValue Value)> enumerable, CancellationToken cancellationToken) where TKey : notnull
    {
        var list = await enumerable.ToArrayAsync(cancellationToken);

        return list.ToFrozenDictionary();
    }
}

public static class DictionaryExtensions
{
    public static Option<TValue> Find<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull =>
        dictionary.TryGetValue(key, out var value)
            ? value
            : Option<TValue>.None;

    public static ImmutableDictionary<TKey, TValue> WhereKey<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> dictionary, Func<TKey, bool> predicate) where TKey : notnull =>
        dictionary.Where(kvp => predicate(kvp.Key))
                  .ToImmutableDictionary();

    public static ImmutableDictionary<TKey, TValue> WhereValue<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> dictionary, Func<TValue, bool> predicate) where TKey : notnull =>
        dictionary.Where(kvp => predicate(kvp.Value))
                  .ToImmutableDictionary();

    public static ImmutableDictionary<TKey2, TValue> MapKey<TKey1, TKey2, TValue>(this IEnumerable<KeyValuePair<TKey1, TValue>> dictionary, Func<TKey1, TKey2> f) where TKey2 : notnull =>
        dictionary.ToImmutableDictionary(kvp => f(kvp.Key), kvp => kvp.Value);

    public static ImmutableDictionary<TKey, TValue2> MapValue<TKey, TValue1, TValue2>(this IEnumerable<KeyValuePair<TKey, TValue1>> dictionary, Func<TValue1, TValue2> f) where TKey : notnull =>
        dictionary.ToImmutableDictionary(kvp => kvp.Key, kvp => f(kvp.Value));

    public static ImmutableDictionary<TKey2, TValue> ChooseKey<TKey, TKey2, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> dictionary, Func<TKey, Option<TKey2>> f) where TKey2 : notnull =>
        dictionary.Choose(kvp => from key2 in f(kvp.Key)
                                 select KeyValuePair.Create(key2, kvp.Value))
                  .ToImmutableDictionary();

    public static ImmutableDictionary<TKey, TValue2> ChooseValue<TKey, TValue1, TValue2>(this IEnumerable<KeyValuePair<TKey, TValue1>> dictionary, Func<TValue1, Option<TValue2>> f) where TKey : notnull =>
        dictionary.Choose(kvp => from value2 in f(kvp.Value)
                                 select KeyValuePair.Create(kvp.Key, value2))
                  .ToImmutableDictionary();
}