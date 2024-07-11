using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public abstract record SubscriptionScope
{
    public sealed record Product : SubscriptionScope
    {
        public required ProductName Name { get; init; }

        public new static Gen<Product> Generate() =>
            from name in ProductModel.GenerateName()
            select new Product { Name = name };
    }

    public sealed record Api : SubscriptionScope
    {
        public required ApiName Name { get; init; }

        public new static Gen<Api> Generate() =>
            from type in ApiType.Generate()
            from name in ApiModel.GenerateName(type)
            select new Api { Name = name };
    }

    public static Gen<SubscriptionScope> Generate() =>
        Gen.OneOf<SubscriptionScope>(Product.Generate(), Api.Generate());
}

public sealed record SubscriptionModel
{
    public required SubscriptionName Name { get; init; }
    public required string DisplayName { get; init; }
    public required SubscriptionScope Scope { get; init; }

    public static Gen<SubscriptionModel> Generate() =>
        from name in GenerateName()
        from scope in SubscriptionScope.Generate()
        select new SubscriptionModel
        {
            Name = name,
            DisplayName = name.ToString(),
            Scope = scope
        };

    public static Gen<SubscriptionName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select SubscriptionName.From(name);

    public static Gen<string> GenerateDisplayName() =>
        Generator.AlphaNumericStringBetween(10, 20);

    public static Gen<FrozenSet<SubscriptionModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10)
                  .DistinctBy(x => x.DisplayName);
}