using common;
using CsCheck;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record LoggerModel : IDtoTestModel<LoggerModel>
{
    public ResourceName Name { get; } = ResourceName.From("azuremonitor").IfErrorThrow();

    public required string Description { get; init; }

    public static IResourceWithDto AssociatedResource { get; } = LoggerResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<LoggerModel> Generate() =>
        from description in GenerateDescription()
        select new LoggerModel
        {
            Description = description
        };

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Sentence();

    private static Gen<LoggerModel> GenerateUpdate(LoggerModel model) =>
        from description in GenerateDescription().OrConst(model.Description)
        select model with
        {
            Description = description
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new LoggerDto()
        {
            Properties = new LoggerDto.LoggerContract
            {
                LoggerType = "azuremonitor",
                Description = Description
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<LoggerDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<LoggerDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Description = overrideDto?.Properties?.Description ?? Description
        };

        var right = new
        {
            Description = jsonDto?.Properties?.Description
        };

        return left.Description.FuzzyEquals(right.Description);
    }
}
