using System.Collections.Frozen;
using CsCheck;

namespace common.tests;

public record ServiceModel
{
    public required FrozenSet<NamedValueModel> NamedValues { get; init; }

    public static Gen<ServiceModel> Generate() =>
        from namedValues in NamedValueModel.GenerateSet()
        select new ServiceModel
        {
            NamedValues = namedValues
        };
}