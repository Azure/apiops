using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record ProductModel
{
    public required ProductName Name { get; init; }
    public required string DisplayName { get; init; }
    public required string State { get; init; }
    public required FrozenSet<ProductPolicyModel> Policies { get; init; }
    public required FrozenSet<ProductGroupModel> Groups { get; init; }
    public required FrozenSet<ProductTagModel> Tags { get; init; }
    public required FrozenSet<ProductApiModel> Apis { get; init; }
    public Option<string> Description { get; init; }
    public Option<string> Terms { get; init; }

    public static Gen<ProductModel> Generate() =>
        from name in GenerateName()
        from displayName in GenerateDisplayName()
        from state in GenerateState()
        from description in GenerateDescription().OptionOf()
        from terms in GenerateTerms().OptionOf()
        from policies in ProductPolicyModel.GenerateSet()
        from groups in ProductGroupModel.GenerateSet()
        from tags in ProductTagModel.GenerateSet()
        from apis in ProductApiModel.GenerateSet()
        select new ProductModel
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Terms = terms,
            State = state,
            Policies = policies,
            Groups = groups,
            Tags = tags,
            Apis = apis
        };

    public static Gen<ProductName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select ProductName.From(name);

    public static Gen<string> GenerateDisplayName() =>
        Generator.AlphaNumericStringBetween(10, 20);

    public static Gen<string> GenerateState() =>
        Gen.OneOfConst("published", "notPublished");

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<string> GenerateTerms() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<FrozenSet<ProductModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10)
                  .DistinctBy(x => x.DisplayName);
}