using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record TagApiModel : ITestModel<TagApiModel>
{
    public required ResourceKey Key { get; init; }

    public required ResourceKey ApiKey { get; init; }

    public JsonObject ToDto() =>
        new JsonObject();

    public Result<Unit> ValidateDto(JsonObject dto) =>
        Unit.Instance;

    public static Gen<ImmutableHashSet<TagApiModel>> GenerateSet(IEnumerable<ITestModel> models)
    {
        var modelsArray = models.ToImmutableArray();
        var tags = modelsArray.OfType<TagModel>();
        var currentRevisionApis = modelsArray.OfType<ApiModel>()
                                             .Where(api => api.Key.Name == api.RootName);

        var tagApis = from tag in tags
                      from api in currentRevisionApis
                      select (tag, api);

        return from tagApiSubSet in Generator.SubSetOf([.. tagApis])
               let tagApiModels = tagApiSubSet.Select(tuple => new TagApiModel
               {
                   Key = new ResourceKey
                   {
                       Resource = TagApiResource.Instance,
                       Name = tuple.api.Key.Name,
                       Parents = tuple.tag.Key.AsParentChain()
                   },
                   ApiKey = tuple.api.Key
               })
               select tagApiModels.ToImmutableHashSet();
    }

    public static Gen<ImmutableHashSet<TagApiModel>> GenerateUpdates(IEnumerable<TagApiModel> models, IEnumerable<ITestModel> allModels) =>
        // Composite resources have nothing to update.
        Gen.Const(ImmutableHashSet<TagApiModel>.Empty);

    public static Gen<ImmutableHashSet<TagApiModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
