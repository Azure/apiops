using LanguageExt;
using System;
using System.Globalization;

namespace common;

public sealed record ApiRevisionNumber
{
    private uint Value { get; }

    private ApiRevisionNumber(uint value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
        Value = value;
    }

    public int ToInt() => (int)Value;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Value}");

    public static ApiRevisionNumber From(int value) => new((uint)value);

    public static Option<ApiRevisionNumber> TryFrom(string? value) =>
        uint.TryParse(value, out var revisionNumber) && revisionNumber > 0
        ? new ApiRevisionNumber(revisionNumber)
        : Option<ApiRevisionNumber>.None;
}