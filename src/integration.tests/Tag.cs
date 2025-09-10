using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record TagModel : IDtoTestModel<TagModel>
{
    public required ResourceName Name { get; init; }

    public static IResourceWithDto AssociatedResource { get; } = TagResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<TagModel> Generate() =>
        from name in Generator.ResourceName
        select new TagModel
        {
            Name = name
        };

    private static Gen<TagModel> GenerateUpdate(TagModel model) =>
        Gen.Const(model);

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new TagDto()
        {
            Properties = new TagDto.TagContract
            {
                DisplayName = Name.ToString()
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson) =>
        true;
}
