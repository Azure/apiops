using CsCheck;
using LanguageExt;
using Nito.Comparers;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record TagModel
{
    public required TagName Name { get; init; }
    public required string DisplayName { get; init; }

    public static Gen<TagModel> Generate() =>
        from name in GenerateName()
        from displayName in GenerateDisplayName()
        select new TagModel
        {
            Name = name,
            DisplayName = displayName
        };

    public static Gen<TagName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select TagName.From(name);

    public static Gen<string> GenerateDisplayName() =>
        Generator.AlphaNumericStringBetween(10, 20);

    public static Gen<FrozenSet<TagModel>> GenerateSet() =>
        Generate().FrozenSetOf(EqualityComparerBuilder.For<TagModel>()
                                                      .EquateBy(x => x.Name)
                                                      .ThenEquateBy(x => x.DisplayName),
                               0, 10);
}