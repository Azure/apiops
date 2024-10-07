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
    public static Gen<Internet> Internet { get; } =
        new GenBogusDataSet<Internet>(() => new Internet());

    public static Gen<Address> Address { get; } =
        new GenBogusDataSet<Address>(() => new Address());

    public static Gen<Lorem> Lorem { get; } =
        new GenBogusDataSet<Lorem>(() => new Lorem());

    public static Gen<Uri> AbsoluteUri { get; } =
        from internet in Internet
        select new Uri(internet.Url());

    public static Gen<Bogus.DataSets.System> BogusSystem { get; } =
        new GenBogusDataSet<Bogus.DataSets.System>(() => new Bogus.DataSets.System());

    public static Gen<DirectoryInfo> DirectoryInfo { get; } =
        from system in BogusSystem
        select new DirectoryInfo(system.FilePath());

    public static Gen<FileInfo> FileInfo { get; } =
        from system in BogusSystem
        select new FileInfo(system.FilePath());

    public static Gen<string> FileName { get; } =
        from system in BogusSystem
        select system.FileName();

    public static Gen<string> NonEmptyString { get; } =
        Gen.String.Where(x => string.IsNullOrWhiteSpace(x) is false);

    public static Gen<JsonValue> JsonValue { get; } =
        Gen.OneOf(Gen.Int.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Float.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.String.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Bool.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.Date.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.DateTime.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)),
                  Gen.DateTimeOffset.Select(x => System.Text.Json.Nodes.JsonValue.Create(x)));

    public static Gen<JsonNode> JsonNode { get; } =
        Gen.Recursive<JsonNode>((iterations, gen) => iterations < 2
                                                       ? Gen.OneOf(JsonValue.Select(x => (JsonNode)x),
                                                                   GetJsonArray(gen).Select(x => (JsonNode)x),
                                                                   GetJsonObject(gen).Select(x => (JsonNode)x))
                                                       : JsonValue.Select(x => (JsonNode)x));

    public static Gen<JsonArray> JsonArray { get; } = GetJsonArray(JsonNode);

    public static Gen<JsonObject> JsonObject { get; } = GetJsonObject(JsonNode);

    public static Gen<string> AlphaNumericStringBetween(int minimumLength, int maximumLength) =>
        Gen.Char
           .AlphaNumeric
           .Array[minimumLength, maximumLength]
           .Select(x => new string(x));

    public static Gen<string> AlphaNumericStringWithLength(int length) =>
        Gen.Char
           .AlphaNumeric
           .Array[length]
           .Select(x => new string(x));

    public static Gen<ImmutableArray<T>> ImmutableArrayOf<T>(this Gen<T> gen) =>
        gen.List
           .Select(x => x.ToImmutableArray());

    public static Gen<ImmutableArray<T>> ImmutableArrayOf<T>(this Gen<T> gen, int minimumCount, int maximumCount) =>
        gen.List[minimumCount, maximumCount]
           .Select(x => x.ToImmutableArray());

    public static Gen<ImmutableArray<T>> SubImmutableArrayOf<T>(ICollection<T> enumerable)
    {
        var array = enumerable.ToArray();

        return from items in Gen.Shuffle(array, 0, array.Length)
               select items.ToImmutableArray();
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T, TKey>(this Gen<T> gen, Func<T, TKey> keySelector, int minimumCount, int maximumCount)
    {
        var comparer = EqualityComparerBuilder.For<T>().EquateBy(keySelector);

        return gen.FrozenSetOf(minimumCount, maximumCount, comparer);
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T, TKey>(this Gen<T> gen, Func<T, TKey> keySelector)
    {
        var comparer = EqualityComparerBuilder.For<T>().EquateBy(keySelector);

        return gen.List
                  .Select(x => x.ToFrozenSet(comparer));
    }

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, IEqualityComparer<T>? comparer = default) =>
        gen.List
           .Select(x => x.ToFrozenSet(comparer));

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, int minimumCount, int maximumCount, IEqualityComparer<T>? comparer = default) =>
        gen.List[minimumCount, maximumCount]
           .Select(x => x.ToFrozenSet(comparer));

    public static Gen<FrozenSet<T>> SubFrozenSetOf<T>(ICollection<T> enumerable, IEqualityComparer<T>? comparer = default)
    {
        var comparerToUse = comparer switch
        {
            null => enumerable switch
            {
                FrozenSet<T> frozenSet => frozenSet.Comparer,
                _ => comparer
            },
            _ => comparer
        };

        return SubImmutableArrayOf(enumerable)
                .Select(x => x.ToFrozenSet(comparerToUse));
    }

    public static Gen<FrozenSet<T>> DistinctBy<T, TKey>(this Gen<FrozenSet<T>> gen, Func<T, TKey> keySelector) =>
        from set in gen
        select set.DistinctBy(keySelector)
                  .ToFrozenSet(set.Comparer);

    public static Gen<Option<T>> OptionOf<T>(this Gen<T> gen) =>
        Gen.Frequency((1, Gen.Const(Option<T>.None)),
                      (4, gen.Select(Option<T>.Some)));

    /// <summary>
    /// Converts a list of generators to a generator of lists
    /// </summary>
    public static Gen<ImmutableArray<T>> SequenceToImmutableArray<T>(this IEnumerable<Gen<T>> gens) =>
        from list in gens.Aggregate(Gen.Const(ImmutableList<T>.Empty),
                                   (accumulate, gen) => from list in accumulate
                                                        from t in gen
                                                        select list.Add(t))
        select list.ToImmutableArray();

    /// <summary>
    /// Converts a list of generators to a generator of frozen sets
    /// </summary>
    public static Gen<FrozenSet<T>> SequenceToFrozenSet<T, TKey>(this IEnumerable<Gen<T>> gens, Func<T, TKey> keySelector) =>
        gens.SequenceToImmutableArray()
            .Select(x => x.ToFrozenSet(keySelector));


    /// <summary>
    /// Converts a list of generators to a generator of frozen sets
    /// </summary>
    public static Gen<FrozenSet<T>> SequenceToFrozenSet<T>(this IEnumerable<Gen<T>> gens, IEqualityComparer<T>? comparer = null) =>
        gens.SequenceToImmutableArray()
            .Select(x => x.ToFrozenSet(comparer));

    public static Gen<FrozenSet<T>> GenerateNewSet<T>(FrozenSet<T> original, Gen<FrozenSet<T>> newGen, Func<T, Gen<T>> updateGen) =>
        GenerateNewSet(original, newGen, updateGen, ChangeParameters.All);

    private static Gen<FrozenSet<T>> GenerateNewSet<T>(FrozenSet<T> original, Gen<FrozenSet<T>> newGen, Func<T, Gen<T>> updateGen, ChangeParameters changeParameters)
    {
        var generator = from originalItems in Gen.Const(original)
                        from itemsRemoved in changeParameters.Remove ? RemoveItems(originalItems) : Gen.Const(originalItems)
                        from itemsAdded in changeParameters.Add ? AddItems(itemsRemoved, newGen) : Gen.Const(itemsRemoved)
                        from itemsModified in changeParameters.Modify ? ModifyItems(itemsAdded, updateGen) : Gen.Const(itemsAdded)
                        select itemsModified;

        return changeParameters.MaxSize.Map(maxSize => generator.SelectMany(set => set.Count <= maxSize
                                                                                    ? generator
                                                                                    : from smallerSet in Gen.Shuffle(set.ToArray(), maxSize)

                                                                                      select smallerSet.ToFrozenSet(set.Comparer)))
                                       .IfNone(generator);
    }

    private static Gen<FrozenSet<T>> RemoveItems<T>(FrozenSet<T> set) =>
        from itemsToRemove in Generator.SubFrozenSetOf(set)
        select set.Except(itemsToRemove, set.Comparer)
                  .ToFrozenSet(set.Comparer);

    private static Gen<FrozenSet<T>> AddItems<T>(FrozenSet<T> set, Gen<FrozenSet<T>> gen) =>
        from itemsToAdd in gen
        select set.Concat(itemsToAdd)
                  .ToFrozenSet(set.Comparer);

    private static Gen<FrozenSet<T>> ModifyItems<T>(FrozenSet<T> set, Func<T, Gen<T>> updateGen) =>
        from itemsToModify in Generator.SubFrozenSetOf(set)
        from modifiedItems in itemsToModify.Select(updateGen).SequenceToImmutableArray()
        select set.Concat(itemsToModify)
                  .Concat(modifiedItems)
                  .ToFrozenSet(set.Comparer);

    private sealed record ChangeParameters
    {
        public required bool Add { get; init; }
        public required bool Modify { get; init; }
        public required bool Remove { get; init; }

        public static ChangeParameters None { get; } = new()
        {
            Add = false,
            Modify = false,
            Remove = false
        };

        public static ChangeParameters All { get; } = new()
        {
            Add = true,
            Modify = true,
            Remove = true
        };

        public Option<int> MaxSize { get; init; } = Option<int>.None;
    }

    private static Gen<JsonArray> GetJsonArray(Gen<JsonNode> nodeGen) =>
        from nodes in nodeGen.Null().Array[0, 5]
        select new JsonArray(nodes);

    private static Gen<JsonObject> GetJsonObject(Gen<JsonNode> nodeGen) =>
        from properties in Gen.Dictionary(NonEmptyString, nodeGen.Null())[0, 5]
        select new JsonObject(properties);
}

/// <summary>
/// Generator for Bogus dataset <typeparamref name="T"/>. It's set to a constant value,
/// and its randomizer is seeded with the <see cref="Gen"/> seed.
/// </summary>
file sealed class GenBogusDataSet<T>(Func<T> creator) : Gen<T> where T : Bogus.DataSet
{
    public override T Generate(PCG pcg, Size? min, out Size size)
    {
        var t = creator();
        t.Random = new Bogus.Randomizer((int)pcg.Seed);

        size = new Size(0);

        return t;
    }
}