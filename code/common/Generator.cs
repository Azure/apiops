using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bogus;
using Bogus.DataSets;
using CsCheck;

namespace common;

public static class Generator
{
    public static Gen<Randomizer> Randomizer { get; } =
        from seed in Gen.Int.Positive
        select new Randomizer(seed);

    public static Gen<Lorem> Word { get; } =
        from randomizer in Randomizer
        select new Lorem()
        {
            Random = randomizer
        };

    public static Gen<Internet> Internet { get; } =
        from randomizer in Randomizer
        select new Internet()
        {
            Random = randomizer
        };

    public static Gen<Bogus.DataSets.System> System { get; } =
        from randomizer in Randomizer
        select new Bogus.DataSets.System()
        {
            Random = randomizer
        };

    public static Gen<ImmutableArray<T>> ImmutableArrayOf<T>(this Gen<T> gen) =>
        from items in gen.List
        select items.ToImmutableArray();

    public static Gen<ImmutableArray<T>> ImmutableArrayOf<T>(this Gen<T> gen, int minimumLength, int maximumLength) =>
        from items in gen.List[minimumLength, maximumLength]
        select items.ToImmutableArray();

    public static Gen<ImmutableArray<T>> SubImmutableArrayOf<T>(ICollection<T> collection) =>
        from items in Gen.Shuffle(collection.ToList(), 0, collection.Count)
        select items.ToImmutableArray();

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, IEqualityComparer<T>? comparer = default) =>
        from items in gen.List
        select items.ToFrozenSet(comparer);

    public static Gen<FrozenSet<T>> FrozenSetOf<T>(this Gen<T> gen, int minimumLength, int maximumLength, IEqualityComparer<T>? comparer = default) =>
        from items in gen.List[minimumLength, maximumLength]
        select items.ToFrozenSet(comparer);

    public static Gen<FrozenSet<T>> SubFrozenSetOf<T>(ICollection<T> collection, IEqualityComparer<T>? comparer = default) =>
        from items in Gen.Shuffle(collection.ToList(), 0, collection.Count)
        let setComparer = GetComparer(collection, comparer)
        select items.ToFrozenSet(setComparer);

    private static IEqualityComparer<T>? GetComparer<T>(IEnumerable<T> enumerable, IEqualityComparer<T>? comparer = default) =>
        comparer switch
        {
            not null => comparer,
            null => enumerable switch
            {
                HashSet<T> hashSet => hashSet.Comparer,
                FrozenSet<T> frozenSet => frozenSet.Comparer,
                ImmutableHashSet<T> immutableHashSet => immutableHashSet.KeyComparer,
                _ => comparer
            }
        };

    public static Gen<ImmutableArray<T2>> TraverseToArray<T, T2>(IEnumerable<T> items, Func<T, Gen<T2>> f) =>
        items.Aggregate(Gen.Const(ImmutableArray<T2>.Empty),
                        (gen, item) => from items in gen
                                       from item2 in f(item)
                                       select items.Add(item2));

    public static Gen<FrozenSet<T2>> TraverseToSet<T, T2>(IEnumerable<T> items, Func<T, Gen<T2>> f, IEqualityComparer<T2>? comparer = default) =>
        items.Aggregate(Gen.Const(ImmutableHashSet.Create(comparer)),
                        (gen, item) => from set in gen
                                       from item2 in f(item)
                                       select set.Add(item2))
             .Select(set => set.ToFrozenSet(comparer));

    public static Gen<FrozenSet<T>> GenerateNewSet<T>(FrozenSet<T> originalSet, Func<T, Gen<T>> updateGen, Gen<FrozenSet<T>> newGen) =>
        from unchangedItems in SubFrozenSetOf(originalSet, originalSet.Comparer)
        from updatedItems in from itemsToUpdate in SubFrozenSetOf(originalSet, originalSet.Comparer)
                             from updatedItems in TraverseToSet(itemsToUpdate, updateGen, originalSet.Comparer)
                             select updatedItems
        from newItems in newGen
        select unchangedItems.Concat(updatedItems)
                             .Concat(newItems)
                             .ToFrozenSet(originalSet.Comparer);
}
