using System;
using System.Collections.Generic;
using System.Linq;

namespace codegen.resources;

public static class StringExtensions
{
    public static string CommaSeparate(this IEnumerable<string> input) =>
        input.Join(", ");

    public static string Join(this IEnumerable<string> input, string separator) =>
        string.Join(separator, input);
}

public static class IEnumerableExtensions
{
    public static IEnumerable<T2> Choose<T, T2>(this IEnumerable<T> enumerable, Func<T, T2?> selector) =>
        from item in enumerable
        let result = selector(item)
        where result is not null
        select result;
}