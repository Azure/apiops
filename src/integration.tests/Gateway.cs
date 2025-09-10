using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record GatewayModel : IDtoTestModel<GatewayModel>
{
    public required ResourceName Name { get; init; }

    public Option<string> Description { get; init; } = Option.None;
    public required string LocationName { get; init; }
    public Option<string> City { get; init; } = Option.None;
    public Option<string> Country { get; init; } = Option.None;

    public static IResourceWithDto AssociatedResource { get; } = GatewayResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<GatewayModel> Generate() =>
        from name in Generator.ResourceName
        from locationName in GenerateLocationName()
        from description in GenerateDescription().OptionOf()
        from city in GenerateCity().OptionOf()
        from country in GenerateCountry().OptionOf()
        select new GatewayModel
        {
            Name = name,
            LocationName = locationName,
            Description = description,
            City = city,
            Country = country
        };

    private static Gen<string> GenerateLocationName() =>
        from lorem in Generator.Lorem
        select lorem.Word();

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<string> GenerateCity() =>
        from address in Generator.Address
        select address.City();

    private static Gen<string> GenerateCountry() =>
        from address in Generator.Address
        select address.Country();

    private static Gen<GatewayModel> GenerateUpdate(GatewayModel model) =>
        from locationName in GenerateLocationName().OrConst(model.LocationName)
        from description in GenerateDescription().OptionOf().OrConst(model.Description)
        from city in GenerateCity().OptionOf().OrConst(model.City)
        from country in GenerateCountry().OptionOf().OrConst(model.Country)
        select model with
        {
            LocationName = locationName,
            Description = description,
            City = city,
            Country = country
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new GatewayDto()
        {
            Properties = new GatewayDto.GatewayContract
            {
                Description = Description.IfNoneNull(),
                LocationData = new GatewayDto.ResourceLocationDataContract
                {
                    Name = LocationName,
                    City = City.IfNoneNull(),
                    CountryOrRegion = Country.IfNoneNull()
                }
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<GatewayDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<GatewayDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Description = overrideDto?.Properties?.Description ?? Description,
            LocationName = overrideDto?.Properties?.LocationData?.Name ?? LocationName,
            City = overrideDto?.Properties?.LocationData?.City ?? City,
            Country = overrideDto?.Properties?.LocationData?.CountryOrRegion ?? Country
        };

        var right = new
        {
            Description = jsonDto?.Properties?.Description,
            LocationName = jsonDto?.Properties?.LocationData?.Name,
            City = jsonDto?.Properties?.LocationData?.City,
            Country = jsonDto?.Properties?.LocationData?.CountryOrRegion
        };

        return left.Description.FuzzyEquals(right.Description)
               && left.LocationName.FuzzyEquals(right.LocationName)
               && left.City.FuzzyEquals(right.City)
               && left.Country.FuzzyEquals(right.Country);
    }

    public bool MatchesDto(JsonObject json) =>
        JsonNodeModule.To<GatewayDto>(json, AssociatedResource.SerializerOptions)
                      .Map(dto => Description.FuzzyEquals(dto.Properties.Description)
                                  && LocationName.FuzzyEquals(dto.Properties.LocationData?.Name)
                                  && City.FuzzyEquals(dto.Properties.LocationData?.City)
                                  && Country.FuzzyEquals(dto.Properties.LocationData?.CountryOrRegion))
                      .IfErrorThrow();
}