using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record ProductApiModel
{
    public required ApiName Name { get; init; }

    public static Gen<ProductApiModel> Generate() =>
        from type in ApiType.Generate()
        from name in ApiModel.GenerateName(type)
        select new ProductApiModel
        {
            Name = name
        };

    public static Gen<FrozenSet<ProductApiModel>> GenerateSet() =>
        Generate().FrozenSetOf(model => model.Name, 0, 10);
}