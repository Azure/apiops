//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;

//namespace common;

//public static class EnumerableExtensions
//{
//    /// <summary>
//    /// Applies <paramref name="chooser"/> to the enumerable and filters out null results.
//    /// </summary>
//    public static IEnumerable<U> Choose<T, U>(this IEnumerable<T> enumerable, Func<T, U?> chooser)
//    {
//        return enumerable.Select(chooser)
//                         .Where(x => x is not null)
//                         .Select(x => x!);
//    }

//    /// <summary>
//    /// Applies <paramref name="chooser"/> to the enumerable and filters out null results.
//    /// </summary>
//    public static IEnumerable<U> Choose<T, U>(this IEnumerable<T> enumerable, Func<T, U?> chooser) where U : struct
//    {
//        return enumerable.Select(chooser)
//                         .Where(x => x.HasValue)
//                         .Select(x => x!.Value);
//    }

//    public static Task ExecuteInParallel<T>(this IAsyncEnumerable<T> source, Func<T, CancellationToken, Task> action, CancellationToken cancellationToken) =>
//        Parallel.ForEachAsync(source,
//                              cancellationToken,
//                              async (t, cancellationToken) => await action(t, cancellationToken));

//    public static Task ExecuteInParallel<T>(this IAsyncEnumerable<T> source, Func<T, Task> action, CancellationToken cancellationToken) =>
//        Parallel.ForEachAsync(source,
//                              cancellationToken,
//                              async (t, cancellationToken) => await action(t));

//    public static Task ExecuteInParallel<T>(this IEnumerable<T> source, Func<T, Task> action, CancellationToken cancellationToken) =>
//        Parallel.ForEachAsync(source,
//                              cancellationToken,
//                              async (t, cancellationToken) => await action(t));

//    public static Task ExecuteInParallel<T>(this IEnumerable<T> source, Func<T, CancellationToken, Task> action, CancellationToken cancellationToken) =>
//        Parallel.ForEachAsync(source,
//                              cancellationToken,
//                              async (t, cancellationToken) => await action(t, cancellationToken));

//    public static IEnumerable<T> Tap<T>(this IEnumerable<T> source, Action<T> action)
//    {
//        foreach (var t in source)
//        {
//            action(t);
//            yield return t;
//        }
//    }

//    /// <summary>
//    /// Applies <paramref name="chooser"/> to the enumerable and returns the first result that is not null.
//    /// If no result has a value, returns the default value
//    /// </summary>
//    /// <returns></returns>
//    public static Task<U?> TryPick<T, U>(this IAsyncEnumerable<T> source, Func<T, U?> chooser, CancellationToken cancellationToken) where U : class
//    {
//        return source.Select(chooser)
//                     .FirstOrDefaultAsync(u => u is not null, cancellationToken)
//                     .AsTask();
//    }

//    /// <summary>
//    /// Applies <paramref name="chooser"/> to the enumerable and returns the first result that has a value.
//    /// If no result has a value, returns the default value
//    /// </summary>
//    /// <returns></returns>
//    public static Task<U?> TryPick<T, U>(this IAsyncEnumerable<T> source, Func<T, U?> chooser, CancellationToken cancellationToken) where U : struct
//    {
//        return source.Select(chooser)
//                     .FirstOrDefaultAsync(u => u.HasValue, cancellationToken)
//                     .AsTask();
//    }

//    public static ILookup<TMappedKey, TValue> MapKeys<TKey, TMappedKey, TValue>(this ILookup<TKey, TValue> lookup, Func<TKey, TMappedKey> mapper)
//    {
//        return lookup.SelectMany(grouping => grouping.Select(value => (Key: mapper(grouping.Key), Value: value)))
//                     .ToLookup(x => x.Key, x => x.Value);
//    }

//    public static ILookup<TKey, TValue> FilterKeys<TKey, TValue>(this ILookup<TKey, TValue> lookup, Func<TKey, bool> predicate)
//    {
//        return lookup.SelectMany(grouping => grouping.Choose(value => predicate(grouping.Key) ? (grouping.Key, value) : new (TKey Key, TValue Value)?()))
//                     .ToLookup(x => x.Key, x => x.Value);
//    }

//    public static ILookup<TKey, TValue> RemoveNullKeys<TKey, TValue>(this ILookup<TKey?, TValue> lookup) where TKey : class
//    {
//        return lookup.FilterKeys(key => key is not null)!;
//    }

//    public static ILookup<TKey, TValue> RemoveNullKeys<TKey, TValue>(this ILookup<TKey?, TValue> lookup) where TKey : struct
//    {
//        return lookup.FilterKeys(key => key.HasValue).MapKeys(key => key!.Value);
//    }

//    public static IEnumerable<TValue> Lookup<TKey, TValue>(this ILookup<TKey, TValue> lookup, TKey key)
//    {
//        return lookup[key];
//    }
//}