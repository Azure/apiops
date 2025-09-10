using common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace integration.tests;

internal static class Extensions
{
    private static readonly IEqualityComparer<string> stringComparer =
        EqualityComparer<string>.Create((first, second) => first!.FuzzyEquals(second),
                                         value => value.Trim().Length.GetHashCode());

    public static bool FuzzyEquals(this string? first, string? second)
    {
        var normalizedFirst = first?.Trim() ?? string.Empty;
        var normalizedSecond = second?.Trim() ?? string.Empty;

        return normalizedFirst.Equals(normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    public static bool FuzzyEqualsPolicy(this string? first, string? second)
    {
        var normalizedFirst = new string([.. first?.Where(c => char.IsWhiteSpace(c) is false) ?? []]);
        var normalizedSecond = new string([.. second?.Where(c => char.IsWhiteSpace(c) is false) ?? []]);

        return stringComparer.Equals(normalizedFirst, normalizedSecond);
    }

    public static bool FuzzyEquals(this Option<string> first, string? second) =>
        first.IfNoneNull().FuzzyEquals(second);

    public static bool FuzzyEquals(this IEnumerable<string> first, IEnumerable<string>? second) =>
        first.ToImmutableHashSet(stringComparer).SetEquals(second ?? []);

    public static bool FuzzyEquals(this float first, float? second) =>
        second.HasValue && first.Equals(second.Value);

    public static bool FuzzyEquals(this float? first, float? second) =>
        first.HasValue
        ? second.HasValue && first.Value.Equals(second.Value)
        : second.HasValue is false;

    public static bool FuzzyEquals(this bool first, bool? second) =>
        second.HasValue && first.Equals(second.Value);

    public static bool FuzzyEquals(this bool? first, bool? second) =>
        first.HasValue
        ? first.Value.Equals(second)
        : second.HasValue is false;
}