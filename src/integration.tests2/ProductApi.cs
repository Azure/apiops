using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ProductApiModel : ITestModel<ProductApiModel>
{
    public required ResourceKey Key { get; init; }

    public required ResourceKey ApiKey { get; init; }

    public JsonObject ToDto() =>
        new JsonObject();

    public Result<Unit> ValidateDto(JsonObject dto) =>
        Unit.Instance;

    public static Gen<ImmutableHashSet<ProductApiModel>> GenerateSet(IEnumerable<ITestModel> models)
    {
        var modelsArray = models.ToImmutableArray();
        var products = modelsArray.OfType<ProductModel>();
        var currentRevisionApis = modelsArray.OfType<ApiModel>()
                                             .Where(api => api.Key.Name == api.RootName);

        var productApis = from product in products
                          from api in currentRevisionApis
                          select (product, api);

        return from productApiSubSet in Generator.SubSetOf([.. productApis])
               let productApiModels = productApiSubSet.Select(tuple => new ProductApiModel
               {
                   Key = new ResourceKey
                   {
                       Resource = ProductApiResource.Instance,
                       Name = tuple.api.Key.Name,
                       Parents = tuple.product.Key.AsParentChain()
                   },
                   ApiKey = tuple.api.Key
               })
               select productApiModels.ToImmutableHashSet();
    }

    public static Gen<ImmutableHashSet<ProductApiModel>> GenerateUpdates(IEnumerable<ProductApiModel> models, IEnumerable<ITestModel> allModels) =>
        // Composite resources have nothing to update.
        Gen.Const(ImmutableHashSet<ProductApiModel>.Empty);

    public static Gen<ImmutableHashSet<ProductApiModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
