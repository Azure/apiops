using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record TagProductModel : ITestModel<TagProductModel>
{
    public required ResourceKey Key { get; init; }

    public required ResourceKey ProductKey { get; init; }

    public JsonObject ToDto() =>
        new JsonObject();

    public Result<Unit> ValidateDto(JsonObject dto) =>
        Unit.Instance;

    public static Gen<ImmutableHashSet<TagProductModel>> GenerateSet(IEnumerable<ITestModel> models)
    {
        var modelsArray = models.ToImmutableArray();
        var tags = modelsArray.OfType<TagModel>();
        var products = modelsArray.OfType<ProductModel>();

        var tagProducts = from tag in tags
                          from product in products
                          select (tag, product);

        return from tagProductSubSet in Generator.SubSetOf([.. tagProducts])
               let tagProductModels = tagProductSubSet.Select(tuple => new TagProductModel
               {
                   Key = new ResourceKey
                   {
                       Resource = TagProductResource.Instance,
                       Name = tuple.product.Key.Name,
                       Parents = tuple.tag.Key.AsParentChain()
                   },
                   ProductKey = tuple.product.Key
               })
               select tagProductModels.ToImmutableHashSet();
    }

    public static Gen<ImmutableHashSet<TagProductModel>> GenerateUpdates(IEnumerable<TagProductModel> models, IEnumerable<ITestModel> allModels) =>
        // Composite resources have nothing to update.
        Gen.Const(ImmutableHashSet<TagProductModel>.Empty);

    public static Gen<ImmutableHashSet<TagProductModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
