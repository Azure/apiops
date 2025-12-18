using Bogus;
using Bogus.DataSets;
using common;
using CsCheck;
using System.Collections.Immutable;

namespace common.tests;

public static class Generator
{
    public static Gen<Randomizer> Randomizer { get; } =
        from seed in Gen.Int.Positive
        select new Randomizer(seed);

    public static Gen<Lorem> Lorem { get; } =
        from randomizer in Randomizer
        select new Lorem { Random = randomizer };

    public static Gen<Internet> Internet { get; } =
        from randomizer in Randomizer
        select new Internet { Random = randomizer };

    public static Gen<Uri> Uri { get; } =
        from internet in Internet
        select new Uri(internet.Url());

    public static Gen<Address> Address { get; } =
        from randomizer in Randomizer
        select new Address { Random = randomizer };

    public static Gen<string> AlphanumericWord { get; } =
        from randomizer in Randomizer
        let word = randomizer.Word()
        where word.All(char.IsLetterOrDigit)
        select word;

    public static Gen<ResourceName> ResourceName { get; } =
        from chars in Gen.Char['a', 'z'].Array[3, 10]
        let name = new string(chars)
        where name.Length <= 25
        select common.ResourceName.From(name)
                                  .IfErrorThrow();

    public static Gen<ServiceDirectory> ServiceDirectory { get; } =
        from characters in Gen.OneOf([Gen.Char['a', 'z'], Gen.Char['0', '9']]).Array[8]
        let directoryName = $"apiops-{new string(characters)}"
        let path = Path.Combine(Path.GetTempPath(), directoryName)
        select common.ServiceDirectory.FromPath(path);

    public static Gen<Option<T>> OptionOf<T>(this Gen<T> gen) =>
        Gen.Frequency((4, from t in gen
                          select Option.Some(t)),
                      (1, Gen.Const(Option<T>.None())));

    public static Gen<T> OrConst<T>(this Gen<T> gen, T t) =>
        Gen.OneOf([gen, Gen.Const(t)]);

    /// <remarks>
    /// If <paramref name="option"/> is Some, the generator should return a Some value from <paramref name="gen"/>.
    /// This addresses issues with publisher overrides.
    /// </remarks>
    public static Gen<Option<T>> OrConst<T>(this Gen<Option<T>> gen, Option<T> option) =>
        Gen.OneOf([option.Map(_ => gen.Where(option => option.IsSome))
                         .IfNone(() => gen),
                   Gen.Const(option)]);

    public static Gen<ImmutableHashSet<T>> SubSetOf<T>(ICollection<T> collection) =>
        SubSetOf(collection, minimumLength: 0, maximumLength: collection.Count);

    public static Gen<ImmutableHashSet<T>> SubSetOf<T>(ICollection<T> collection, int minimumLength, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, minimumLength);

        return from length in Gen.Int[minimumLength, Math.Min(collection.Count, maximumLength)]
               from set in length switch
               {
                   0 => Gen.Const(ImmutableHashSet<T>.Empty),
                   _ => from list in Gen.Shuffle(constants: [.. collection], length)
                        select list.ToImmutableHashSet()
               }
               select set;
    }

    public static Gen<ImmutableHashSet<T>> HashSetOf<T>(this Gen<T> gen, IEqualityComparer<T>? comparer = default) =>
        gen.HashSetOf(minimumLength: 0, maximumLength: 10, comparer);

    public static Gen<ImmutableHashSet<T>> HashSetOf<T>(this Gen<T> gen, int length, IEqualityComparer<T>? comparer = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return gen.HashSetOf(minimumLength: length, maximumLength: length, comparer);
    }

    public static Gen<ImmutableHashSet<T>> HashSetOf<T>(this Gen<T> gen, int minimumLength, int maximumLength, IEqualityComparer<T>? comparer = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, minimumLength);

        return from items in gen.Array[minimumLength, maximumLength]
               let set = comparer is null
                   ? [.. items]
                   : items.ToImmutableHashSet(comparer)
               where set.Count >= minimumLength && set.Count <= maximumLength
               select set;
    }

    public static Gen<ImmutableArray<T2>> Traverse<T1, T2>(IEnumerable<T1> source, Func<T1, Gen<T2>> f) =>
        source.Aggregate(Gen.Const(ImmutableArray.Create<T2>()),
                         (arrayGen, t1) => from array in arrayGen
                                           from t2 in f(t1)
                                           select array.Add(t2));

    public static Gen<ImmutableArray<(T3, T2)>> Traverse<T1, T2, T3>(IEnumerable<(T1, T2)> source, Func<T1, Gen<T3>> f) =>
        Generator.Traverse(source,
                           item => from t3 in f(item.Item1)
                                   select (t3, item.Item2));
}