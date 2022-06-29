using System;
using System.Collections.Generic;
using System.Linq;

namespace common;

public static class IEnumerableModule
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

}
