using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record ProductGroupModel
{
    public required GroupName Name { get; init; }

    public static Gen<ProductGroupModel> Generate() =>
        from name in GroupModel.GenerateName()
        select new ProductGroupModel
        {
            Name = name
        };

    public static Gen<FrozenSet<ProductGroupModel>> GenerateSet() =>
        Generate().FrozenSetOf(model => model.Name, 0, 10);
}