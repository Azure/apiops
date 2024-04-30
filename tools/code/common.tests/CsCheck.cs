using Bogus.DataSets;
using CsCheck;
using LanguageExt;
using Nito.Comparers;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace common.tests;

public static class Generator
{
    public static Gen<Internet> Internet { get; } = Gen.Const(new Internet());

    public static Gen<Address> Address { get; } = Gen.Const(new Address());

    public static Gen<Lorem> Lorem { get; } = Gen.Const(new Lorem());

    public static Gen<Uri> AbsoluteUri { get; } =
        from internet in Internet
        select new Uri(internet.Url());

    public static Gen<Bogus.DataSets.System> BogusSystem { get; } = Gen.Const(new Bogus.DataSets.System());

    public static Gen<DirectoryInfo> DirectoryInfo { get; } =
        from system in BogusSystem
        select new DirectoryInfo(system.FilePath());

    public static Gen<FileInfo> FileInfo { get; } =
        from system in BogusSystem
        select new FileInfo(system.FilePath());

    public static Gen<string> NonEmptyString { get; } =
        Gen.String.Where(x => string.IsNullOrWhiteSpace(x) is false);

    public static Gen<JsonValue> JsonValue { get; } = GenerateJsonValue();

    public static Gen<JsonObject> JsonObject { get; } = GenerateJsonObject();

    public static Gen<JsonArray> JsonArray { get; } = GenerateJsonArray();

    public static Gen<JsonNode> JsonNode { get; } = GenerateJsonNode();

    private static Gen<JsonValue> GenerateJsonValue() =>
        Gen.OneOf(Gen.Bool.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Byte.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Char.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.DateTime.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.DateTimeOffset.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Decimal.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Double.Where(double.IsFinite).Where(x => double.IsNaN(x) is false).Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Float.Where(float.IsFinite).Where(x => float.IsNaN(x) is false).Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Guid.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Int.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Long.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.SByte.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Short.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.String.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.UInt.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.ULong.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.UShort.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)));

    private static Gen<JsonObject> GenerateJsonObject() =>
        GenerateJsonObject(GenerateJsonNode(), limits: 100);

    private static Gen<JsonObject> GenerateJsonObject(Gen<JsonNode> nodeGen, uint limits) =>
        Gen.Dictionary(Gen.String.AlphaNumeric,
                       nodeGen.Null())[0, (int)limits]
           .Select(x => new JsonObject(x));

    private static Gen<JsonArray> GenerateJsonArray() =>
        GenerateJsonArray(GenerateJsonNode(), limits: 100);

    private static Gen<JsonArray> GenerateJsonArray(Gen<JsonNode> nodeGen, uint limits) =>
        nodeGen.Null()
               .Array[0, (int)limits]
               .Select(x => new JsonArray(x));

    private static Gen<JsonNode> GenerateJsonNode() =>
        Gen.Recursive<JsonNode>((depth, gen) =>
            depth == 3
            ? GenerateJsonValue().Select(x => x as JsonNode)
            : Gen.OneOf(GenerateJsonValue().Select(x => x as JsonNode),
                        GenerateJsonObject(gen, limits: (uint)depth).Select(x => x as JsonNode),
                        GenerateJsonArray(gen, limits: (uint)depth).Select(x => x as JsonNode)));

    public static Gen<string> AlphaNumericStringWithLength(int length) =>
        Gen.Char.AlphaNumeric.Array[length].Select(x => new string(x));

    public static Gen<string> AlphaNumericStringBetween(int minimumLength, int maximumLength) =>
        Gen.Char.AlphaNumeric.Array[minimumLength, maximumLength].Select(x => new string(x));

    public static Gen<ImmutableArray<T>> ImmutableArrayOf<T>(this Gen<T> gen) =>
        gen.List.Select(x => x.ToImmutableArray());

    public static Gen<ImmutableArray<T>> ImmutableArrayOf<T>(this Gen<T> gen, int minimumCount, int maximumCount) =>
        gen.List[minimumCount, maximumCount].Select(x => x.ToImmutableArray());

    public static Gen<ImmutableArray<T>> SubImmutableArrayOf<T>(IEnumerable<T> enumerable)
    {
        var array = enumerable.ToArray();

        return from items in Gen.Shuffle(array, 0, array.Length)
               select items.ToImmutableArray();
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen) =>
        gen.List.Select(x => x.ToFrozenSet());

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, int minimumCount, int maximumCount)
    {
        if (maximumCount < minimumCount)
        {
            throw new ArgumentException("Maximum count must be greater than or equal to minimum count.", nameof(maximumCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumCount, nameof(minimumCount));

        return gen.List[minimumCount, maximumCount].Select(x => x.ToFrozenSet());
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, IEqualityComparer<T> comparer) =>
        gen.List.Select(x => x.ToFrozenSet(comparer));

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, IEqualityComparer<T> comparer, int minimumCount, int maximumCount)
    {
        if (maximumCount < minimumCount)
        {
            throw new ArgumentException("Maximum count must be greater than or equal to minimum count.", nameof(maximumCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumCount, nameof(minimumCount));

        return gen.List[minimumCount, maximumCount].Select(x => x.ToFrozenSet(comparer));
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T, TKey>(this Gen<T> gen, Func<T, TKey> keySelector)
    {
        var comparer = EqualityComparerBuilder.For<T>().EquateBy(keySelector);

        return gen.FrozenSetOf(comparer);
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T, TKey>(this Gen<T> gen, Func<T, TKey> keySelector, int minimumCount, int maximumCount)
    {
        var comparer = EqualityComparerBuilder.For<T>().EquateBy(keySelector);

        return gen.FrozenSetOf(comparer, minimumCount, maximumCount);
    }

    public static Gen<FrozenSet<T>> SubFrozenSetOf<T>(IEnumerable<T> enumerable) =>
        SubImmutableArrayOf(enumerable)
           .Select(x => x.ToFrozenSet());

    public static Gen<FrozenSet<T>> SubFrozenSetOf<T>(IEnumerable<T> enumerable, IEqualityComparer<T> comparer) =>
        SubImmutableArrayOf(enumerable)
           .Select(x => x.ToFrozenSet(comparer));

    public static Gen<FrozenSet<T>> SubFrozenSetOf<T, TKey>(IEnumerable<T> enumerable, Func<T, TKey> keySelector)
    {
        var comparer = EqualityComparerBuilder.For<T>().EquateBy(keySelector);

        return SubFrozenSetOf(enumerable, comparer);
    }

    public static Gen<Option<T>> OptionOf<T>(this Gen<T> gen) =>
        Gen.Frequency((1, Gen.Const(Option<T>.None)),
                      (4, gen.Select(Option<T>.Some)));

    public static Gen<string> ToUpperInvariant(this GenString gen) =>
        gen.Select(x => x.ToUpperInvariant());

    public static Gen<Option<T>> Sequence<T>(this Option<Gen<T>> option) =>
        option.Match(gen => gen.Select(Option<T>.Some),
                     () => Gen.Const(Option<T>.None));

    public static Gen<FrozenSet<T>> SequenceToFrozenSet<T, TKey>(this IEnumerable<Gen<T>> gens, Func<T, TKey> keySelector) =>
        gens.SequenceToImmutableArray()
            .Select(x => x.ToFrozenSet(keySelector));

    public static Gen<FrozenSet<T>> SequenceToFrozenSet<T>(this IEnumerable<Gen<T>> gens, IEqualityComparer<T>? comparer = null) =>
        gens.SequenceToImmutableArray()
            .Select(x => x.ToFrozenSet(comparer));

    /// <summary>
    /// Converts a list of generators to a generator of lists
    /// </summary>
    public static Gen<ImmutableArray<T>> SequenceToImmutableArray<T>(this IEnumerable<Gen<T>> gens) =>
        from list in gens.Aggregate(Gen.Const(ImmutableList<T>.Empty),
                                   (accumulate, gen) => from list in accumulate
                                                        from t in gen
                                                        select list.Add(t))
        select list.ToImmutableArray();
}