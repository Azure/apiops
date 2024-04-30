using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record ProductTagModel
{
    public required TagName Name { get; init; }

    public static Gen<ProductTagModel> Generate() =>
        from name in TagModel.GenerateName()
        select new ProductTagModel
        {
            Name = name
        };

    public static Gen<FrozenSet<ProductTagModel>> GenerateSet() =>
        Generate().FrozenSetOf(model => model.Name, 0, 10);
}