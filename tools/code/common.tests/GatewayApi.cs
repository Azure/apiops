using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record GatewayApiModel
{
    public required ApiName Name { get; init; }

    public static Gen<GatewayApiModel> Generate() =>
        from type in ApiType.Generate()
        from name in ApiModel.GenerateName(type)
        select new GatewayApiModel
        {
            Name = name
        };

    public static Gen<FrozenSet<GatewayApiModel>> GenerateSet() =>
        Generate().FrozenSetOf(model => model.Name, 0, 10);
}