using common;
using common.tests;
using CsCheck;
using System;
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
        new()
        {
            ["name"] = Key.Name.ToString(),
            ["properties"] = new JsonObject
            {
                ["apiId"] = $"/{ApiKey}"
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateName()
               from __ in validateApiId()
               select Unit.Instance;

        Result<Unit> validateName() =>
            from name in dto.GetStringProperty("name")
            from unit in name.Equals(Key.Name.ToString(), StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has name '{name}' instead of '{Key.Name}'.")
            select unit;

        Result<Unit> validateApiId() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from apiId in properties.GetStringProperty("apiId")
            from unit in apiId.EndsWith($"{ApiKey}", StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has apiId '{apiId}' that does not end with '/{ApiKey}'.")
            select unit;
    }

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
               select productApiSubSet.Select(tuple => new ProductApiModel
               {
                   Key = new ResourceKey
                   {
                       Resource = ProductApiResource.Instance,
                       Name = tuple.api.Key.Name,
                       Parents = tuple.product.Key.AsParentChain()
                   },
                   ApiKey = tuple.api.Key
               }).ToImmutableHashSet();
    }

    public static Gen<ImmutableHashSet<ProductApiModel>> GenerateUpdates(IEnumerable<ProductApiModel> models, IEnumerable<ITestModel> allModels) =>
        // Link resources have nothing to update.
        Gen.Const(ImmutableHashSet<ProductApiModel>.Empty);

    public static Gen<ImmutableHashSet<ProductApiModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
