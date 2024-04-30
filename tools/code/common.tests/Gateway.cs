using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record GatewayLocation
{
    public required string Name { get; init; }
    public Option<string> City { get; init; }
    public Option<string> CountryOrRegion { get; init; }
    public Option<string> District { get; init; }

    public static Gen<GatewayLocation> Generate() =>
        from address in Generator.Address
        let name = address.CountryCode()
        from city in Gen.Const(address.City()).OptionOf()
        from country in Gen.Const(address.Country()).OptionOf()
        from district in Gen.Const(address.County()).OptionOf()
        select new GatewayLocation
        {
            Name = name,
            City = city,
            CountryOrRegion = country,
            District = district
        };
}

public sealed record GatewayModel
{
    public required GatewayName Name { get; init; }
    public Option<string> Description { get; init; }
    public required GatewayLocation Location { get; init; }
    public required FrozenSet<GatewayApiModel> Apis { get; init; }

    public static Gen<GatewayModel> Generate() =>
        from name in GenerateName()
        from description in GenerateDescription().OptionOf()
        from location in GatewayLocation.Generate()
        from apis in GatewayApiModel.GenerateSet()
        select new GatewayModel
        {
            Name = name,
            Description = description,
            Location = location,
            Apis = apis
        };

    public static Gen<GatewayName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select GatewayName.From(name);

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<FrozenSet<GatewayModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10);
}