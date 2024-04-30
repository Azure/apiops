using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record ApiTagModel
{
    public required TagName Name { get; init; }

    public static Gen<ApiTagModel> Generate() =>
        from name in TagModel.GenerateName()
        select new ApiTagModel
        {
            Name = name
        };

    public static Gen<FrozenSet<ApiTagModel>> GenerateSet() =>
        Generate().FrozenSetOf(model => model.Name, 0, 10);
}