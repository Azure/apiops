using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record GroupModel : IDtoTestModel<GroupModel>
{
    public required ResourceName Name { get; init; }
    public required Option<string> Description { get; init; }

    public static IResourceWithDto AssociatedResource { get; } = GroupResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<GroupModel> Generate() =>
        from name in Generator.ResourceName
        from description in GenerateDescription().OptionOf()
        select new GroupModel
        {
            Name = name,
            Description = description
        };

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<GroupModel> GenerateUpdate(GroupModel model) =>
        from description in GenerateDescription().OptionOf().OrConst(model.Description)
        select model with
        {
            Description = description
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new GroupDto()
        {
            Properties = new GroupDto.GroupContract
            {
                DisplayName = Name.ToString(),
                Description = Description.IfNoneNull()
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<GroupDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<GroupDto>(json, AssociatedResource.SerializerOptions)
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