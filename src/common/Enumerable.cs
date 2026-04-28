using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

/// <summary>
/// Provides extension methods for working with IEnumerable&lt;T&gt; in a functional style.
/// </summary>
public static class EnumerableExtensions
{
    /// <summary>
    /// Returns the first element as an option.
    /// </summary>
    /// <returns><c>Some(first element)</c> if the sequence has elements, otherwise <see cref="common.None"/>.</returns>
    public static Option<T> Head<T>(this IEnumerable<T> source) =>
        source.Select(Option.Some)
              .DefaultIfEmpty(Option.None)
              .First();

    /// <summary>
    /// Returns the first element where <paramref name="predicate"/> is true as an option.
    /// </summary>
    /// <returns><c>Some(first matching element)</c> if one matches the predicate, otherwise <see cref="common.None"/>.</returns>
    public static Option<T> Head<T>(this IEnumerable<T> source, Func<T, bool> predicate) =>
        source.Where(predicate)
              .Head();

    /// <summary>
    /// Returns the single element as an option.
    /// </summary>
    /// <returns><c>Some(element)</c> if the sequence contains exactly one element, otherwise <see cref="common.None"/>.</returns>
    public static Option<T> SingleOrNone<T>(this IEnumerable<T> source)
    {
        using var enumerator = source.GetEnumerator();

        if (enumerator.MoveNext() is false)
        {
            return Option.None; // No elements
        }

        var item = Option.Some(enumerator.Current);

        return enumerator.MoveNext()
                ? Option.None // More than one element
                : item; // Exactly one element
    }

    /// <summary>
    /// Filters and transforms elements using <paramref name="selector"/>.
    /// </summary>
    /// <returns>A sequence containing only the values where <paramref name="selector"/> returned Some.</returns>
    public static IEnumerable<T2> Choose<T, T2>(this IEnumerable<T> source, Func<T, Option<T2>> selector) =>
        source.Select(selector)
              .Where(option => option.IsSome)
              .Select(option => option.IfNone(() => throw new UnreachableException("All options should be in the 'Some' state.")));

    /// <summary>
    /// Asynchronously filters and transforms elements using <paramref name="selector"/>.
    /// </summary>
    /// <returns>An async sequence containing only the values where <paramref name="selector"/> returned Some.</returns>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IEnumerable<T> source, Func<T, ValueTask<Option<T2>>> selector) =>
        source.ToAsyncEnumerable()
              .Choose(selector);

    /// <summary>
    /// Returns the first element that produces Some when transformed by <paramref name="selector"/>.
    /// </summary>
    /// <returns>The first Some value, or <see cref="common.None"/> if no element produces Some.</returns>
    public static Option<T2> Pick<T, T2>(this IEnumerable<T> source, Func<T, Option<T2>> selector) =>
        source.Select(selector)
              .Where(option => option.IsSome)
              .DefaultIfEmpty(Option.None)
              .First();

    /// <summary>
    /// Applies <paramref name="selector"/> to each element, collecting successes or aggregating errors.
    /// </summary>
    /// <returns>Success with all results if all succeed, otherwise combined errors.</returns>
    public static Result<ImmutableArray<T2>> Traverse<T, T2>(this IEnumerable<T> source, Func<T, Result<T2>> selector, CancellationToken cancellationToken)
    {
        var results = new List<T2>();
        var errors = new List<Error>();

        source.Iter(item => selector(item).Match(results.Add, errors.Add),
                    cancellationToken);

        return errors.Count > 0
                ? errors.Aggregate((first, second) => first + second)
                : Result.Success(results.ToImmutableArray());
    }

    /// <summary>
    /// Applies <paramref name="selector"/> to each element, succeeding only if all elements succeed.
    /// </summary>
    /// <returns><c>Some</c> with all results if all succeed, otherwise <see cref="common.None"/>.</returns>
    public static Option<ImmutableArray<T2>> Traverse<T, T2>(this IEnumerable<T> source, Func<T, Option<T2>> selector, CancellationToken cancellationToken)
    {
        var results = new List<T2>();

        foreach (var t in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasNone = false;

            selector(t).Match(results.Add, () => hasNone = true);

            if (hasNone)
            {
                return Option.None;
            }
        }

        return Option.Some(results.ToImmutableArray());
    }

    /// <summary>
    /// Executes <paramref name="action"/> on each element sequentially.
    /// </summary>
    public static void Iter<T>(this IEnumerable<T> source, Action<T> action, CancellationToken cancellationToken = default) =>
        source.IterParallel(action, maxDegreeOfParallelism: 1, cancellationToken);

    /// <summary>
    /// Asynchronously executes <paramref name="action"/> on each element sequentially.
    /// </summary>
    public static ValueTask IterTask<T>(this IEnumerable<T> source, Func<T, ValueTask> action, CancellationToken cancellationToken = default) =>
        source.IterTaskParallel(action, maxDegreeOfParallelism: 1, cancellationToken);

    /// <summary>
    /// Executes <paramref name="action"/> on each element in parallel.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. <see cref="common.None"/> for unbounded.</param>
    public static void IterParallel<T>(this IEnumerable<T> source, Action<T> action, Option<int> maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        maxDegreeOfParallelism.Iter(max => options.MaxDegreeOfParallelism = max);

        Parallel.ForEach(source, options, item => action(item));
    }

    /// <summary>
    /// Asynchronously executes <paramref name="action"/> on each element in parallel.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. <see cref="common.None"/> for unbounded.</param>
    public static async ValueTask IterTaskParallel<T>(this IEnumerable<T> source, Func<T, ValueTask> action, Option<int> maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        maxDegreeOfParallelism.Iter(max => options.MaxDegreeOfParallelism = max);

        await Parallel.ForEachAsync(source, options, async (item, _) => await action(item));
    }

    /// <summary>
    /// Executes <paramref name="action"/> on each element as it's enumerated, returning the original sequence unchanged.
    /// </summary>
    public static IEnumerable<T> Tap<T>(this IEnumerable<T> source, Action<T> action) =>
        source.Select(item =>
        {
            action(item);
            return item;
        });

    /// <summary>
    /// Separates tuples into two immutable arrays. Inverse of <c>Zip</c>.
    /// </summary>
    public static (ImmutableArray<T1>, ImmutableArray<T2>) Unzip<T1, T2>(this IEnumerable<(T1, T2)> source)
    {
        var list1 = new List<T1>();
        var list2 = new List<T2>();

        foreach (var (item1, item2) in source)
        {
            list1.Add(item1);
            list2.Add(item2);
        }

        return ([.. list1], [.. list2]);
    }
}

/// <summary>
/// Provides extension methods for working with IAsyncEnumerable&lt;T&gt; in a functional style.
/// </summary>
public static class AsyncEnumerableExtensions
{
    /// <summary>
    /// Returns the first element of the async sequence as an option.
    /// </summary>
    /// <returns><c>Some(first element)</c> if the sequence has elements, otherwise <see cref="common.None"/>.</returns>
    public static async ValueTask<Option<T>> Head<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken) =>
        await source.Select(Option.Some)
                    .DefaultIfEmpty(Option.None)
                    .FirstAsync(cancellationToken);

    /// <summary>
    /// Filters and transforms async elements using <paramref name="selector"/>.
    /// </summary>
    /// <returns>An async sequence containing only the values where <paramref name="selector"/> returned Some.</returns>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> source, Func<T, Option<T2>> selector) =>
        source.Select(selector)
              .Where(option => option.IsSome)
              .Select(option => option.IfNone(() => throw new UnreachableException("All options should be in the 'Some' state.")));

    /// <summary>
    /// Asynchronously filters and transforms async elements using <paramref name="selector"/>.
    /// </summary>
    /// <returns>An async sequence containing only the values where <paramref name="selector"/> returned Some.</returns>
    public static IAsyncEnumerable<T2> Choose<T, T2>(this IAsyncEnumerable<T> source, Func<T, ValueTask<Option<T2>>> selector) =>
        source.Select(async (T item, CancellationToken _) => await selector(item))
              .Where(option => option.IsSome)
              .Select(option => option.IfNone(() => throw new UnreachableException("All options should be in the 'Some' state.")));

    /// <summary>
    /// Returns the first async element that produces Some when transformed by <paramref name="selector"/>.
    /// </summary>
    /// <returns>The first Some value, or <see cref="common.None"/> if no element produces Some.</returns>
    public static async ValueTask<Option<T2>> Pick<T, T2>(this IAsyncEnumerable<T> source, Func<T, Option<T2>> selector, CancellationToken cancellationToken) =>
        await source.Select(selector)
                    .Where(option => option.IsSome)
                    .DefaultIfEmpty(Option.None)
                    .FirstAsync(cancellationToken);

    /// <summary>
    /// Asynchronously applies <paramref name="selector"/> to each element, collecting successes or aggregating errors.
    /// </summary>
    /// <returns>Success with all results if all succeed, otherwise combined errors.</returns>
    public static async ValueTask<Result<ImmutableArray<T2>>> Traverse<T, T2>(this IAsyncEnumerable<T> source, Func<T, ValueTask<Result<T2>>> selector, CancellationToken cancellationToken)
    {
        var results = new List<T2>();
        var errors = new List<Error>();

        await source.IterTask(async item =>
        {
            var result = await selector(item);
            result.Match(results.Add, errors.Add);
        }, cancellationToken);

        return errors.Count > 0
                ? errors.Aggregate((first, second) => first + second)
                : Result.Success(results.ToImmutableArray());
    }

    /// <summary>
    /// Asynchronously applies <paramref name="selector"/> to each element, succeeding only if all elements succeed.
    /// </summary>
    /// <returns><c>Some</c> with all results if all succeed, otherwise <see cref="common.None"/>.</returns>
    public static async ValueTask<Option<ImmutableArray<T2>>> Traverse<T, T2>(this IAsyncEnumerable<T> source, Func<T, ValueTask<Option<T2>>> selector, CancellationToken cancellationToken)
    {
        var results = new List<T2>();
        var hasNone = false;

        await source.IterTask(async item =>
        {
            var option = await selector(item);
            option.Match(results.Add, () => hasNone = true);
        }, cancellationToken);

        return hasNone
                ? Option.None
                : Option.Some(results.ToImmutableArray());
    }

    /// <summary>
    /// Asynchronously executes <paramref name="action"/> on each element sequentially.
    /// </summary>
    public static ValueTask IterTask<T>(this IAsyncEnumerable<T> source, Func<T, ValueTask> action, CancellationToken cancellationToken = default) =>
        source.IterTaskParallel(action, maxDegreeOfParallelism: 1, cancellationToken);

    /// <summary>
    /// Asynchronously executes <paramref name="action"/> on each element in parallel.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum degree of parallelism. <see cref="common.None"/> for unbounded.</param>
    public static async ValueTask IterTaskParallel<T>(this IAsyncEnumerable<T> source, Func<T, ValueTask> action, Option<int> maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        maxDegreeOfParallelism.Iter(max => options.MaxDegreeOfParallelism = max);

        await Parallel.ForEachAsync(source, options, async (item, _) => await action(item));
    }

    /// <summary>
    /// Executes <paramref name="action"/> on each async element as it's enumerated, returning the original sequence unchanged.
    /// </summary>
    public static IAsyncEnumerable<T> Tap<T>(this IAsyncEnumerable<T> source, Action<T> action) =>
        source.Select(item =>
        {
            action(item);
            return item;
        });

    /// <summary>
    /// Asynchronously executes <paramref name="action"/> on each async element as it's enumerated, returning the original sequence unchanged.
    /// </summary>
    public static IAsyncEnumerable<T> TapTask<T>(this IAsyncEnumerable<T> source, Func<T, ValueTask> action) =>
        source.Select(async (T item, CancellationToken _) =>
        {
            await action(item);
            return item;
        });

    /// <summary>
    /// Asynchronously separates tuples into two immutable arrays. Async inverse of <c>Zip</c>.
    /// </summary>
    public static async ValueTask<(ImmutableArray<T1>, ImmutableArray<T2>)> Unzip<T1, T2>(this IAsyncEnumerable<(T1, T2)> source, CancellationToken cancellationToken)
    {
        var list1 = new List<T1>();
        var list2 = new List<T2>();

        await foreach (var (item1, item2) in source.WithCancellation(cancellationToken))
        {
            list1.Add(item1);
            list2.Add(item2);
        }

        return ([.. list1], [.. list2]);
    }
}

/// <summary>
/// Provides extension methods for safe dictionary operations.
/// </summary>
public static class DictionaryExtensions
{
    /// <summary>
    /// Returns the value for <paramref name="key"/> as an option.
    /// </summary>
    /// <returns><c>Some(value)</c> if the key exists, otherwise <see cref="common.None"/>.</returns>
    public static Option<TValue> Find<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key) =>
        dictionary.TryGetValue(key, out var value)
            ? Option.Some(value)
            : Option.None;
}