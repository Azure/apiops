using MoreLinq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class IEnumerableExtensions
{
    /// <summary>
    /// Applies <paramref name="f"/> to <paramref name="enumerable"/> items and filters out
    /// null results.
    /// </summary>
    public static IEnumerable<T2> Choose<T, T2>(this IEnumerable<T> enumerable, Func<T, T2?> f) where T2 : class
    {
        return from t in enumerable
               let t2 = f(t)
               where t2 is not null
               select t2;
    }

    /// <summary>
    /// Applies <paramref name="f"/> to <paramref name="enumerable"/> items and filters out
    /// null results.
    /// </summary>
    public static IEnumerable<T2> Choose<T, T2>(this IEnumerable<T> enumerable, Func<T, T2?> f) where T2 : struct
    {
        return from t in enumerable
               let t2 = f(t)
               where t2 is not null
               select t2.Value;
    }

    public static void ForEach<T>(this IEnumerable<T> enumerable, Action<T> action)
    {
        foreach (var t in enumerable)
        {
            action(t);
        }
    }

    public static async ValueTask ForEachParallel<T>(this IEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await Parallel.ForEachAsync(enumerable,
                                    cancellationToken,
                                    (item, token) => action(item));

    /// <summary>
    /// Returns an empty enumerable if <paramref name="enumerable"/> is null.
    /// </summary>
    public static IEnumerable<T> IfNullEmpty<T>(this IEnumerable<T>? enumerable) =>
        enumerable is null ? Enumerable.Empty<T>() : enumerable;

    public static IEnumerable<T> FullJoin<T, TKey>(this IEnumerable<T> first, IEnumerable<T> second, Func<T, TKey> keySelector, Func<T, T, T> bothSelector) =>
        first.FullJoin(second, keySelector, t => t, t => t, bothSelector);

    public static IEnumerable<T> LeftJoin<T, TKey>(this IEnumerable<T> first, IEnumerable<T> second, Func<T, TKey> keySelector, Func<T, T, T> bothSelector) =>
        first.LeftJoin(second, keySelector, t => t, bothSelector);
}

public static class IAsyncEnumerableExtensions
{
    public static async ValueTask ForEachParallel<T>(this IAsyncEnumerable<T> enumerable, Func<T, ValueTask> action, CancellationToken cancellationToken) =>
        await Parallel.ForEachAsync(enumerable,
                                    cancellationToken,
                                    (item, token) => action(item));
}

internal static class ObjectExtensions
{
    /// <summary>
    /// Applies <paramref name="f"/> to <paramref name="t"/> if it is not null, otherwise
    /// returns the default value of <typeparamref name="T2"/>
    /// </summary>
    public static T2? Map<T, T2>(this T? t, Func<T, T2> f)
    {
        return t is null ? default : f(t);
    }

    /// <summary>
    /// Applies <paramref name="f"/> to <paramref name="t"/> if it is not null, otherwise
    /// returns the default value of <typeparamref name="T2". Differs from <see cref="ObjectExtensions.Map{T, T2}(T?, Func{T, T2})"/>
    /// in that <paramref name="f"/> can return a nullable value./>
    /// </summary>
    public static T2? Bind<T, T2>(this T? t, Func<T, T2?> f)
    {
        return t is null ? default : f(t);
    }
}
