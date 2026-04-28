using common;
using common.tests;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ProductGroupModel : ITestModel<ProductGroupModel>
{
    public required ResourceKey Key { get; init; }

    public required ResourceKey GroupKey { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["name"] = Key.Name.ToString(),
            ["properties"] = new JsonObject
            {
                ["groupId"] = $"/{GroupKey}"
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateName()
               from __ in validateGroupId()
               select Unit.Instance;

        Result<Unit> validateName() =>
            from name in dto.GetStringProperty("name")
            from unit in name.Equals(Key.Name.ToString(), StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has name '{name}' instead of '{Key.Name}'.")
            select unit;

        Result<Unit> validateGroupId() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from groupId in properties.GetStringProperty("groupId")
            from unit in groupId.EndsWith($"{GroupKey}", StringComparison.OrdinalIgnoreCase)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has groupId '{groupId}' that does not end with '/{GroupKey}'.")
            select unit;
    }

    public static Gen<ImmutableHashSet<ProductGroupModel>> GenerateSet(IEnumerable<ITestModel> models)
    {
        var modelsArray = models.ToImmutableArray();
        var products = modelsArray.OfType<ProductModel>();
        var groups = modelsArray.OfType<GroupModel>();

        var productGroups = from product in products
                            from @group in groups
                            select (product, @group);

        return from productGroupSubSet in Generator.SubSetOf([.. productGroups])
               select productGroupSubSet.Select(tuple => new ProductGroupModel
               {
                   Key = new ResourceKey
                   {
                       Resource = ProductGroupResource.Instance,
                       Name = tuple.@group.Key.Name,
                       Parents = tuple.product.Key.AsParentChain()
                   },
                   GroupKey = tuple.@group.Key
               }).ToImmutableHashSet();
    }

    public static Gen<ImmutableHashSet<ProductGroupModel>> GenerateUpdates(IEnumerable<ProductGroupModel> models, IEnumerable<ITestModel> allModels) =>
        // Link resources have nothing to update
        Gen.Const(ImmutableHashSet<ProductGroupModel>.Empty);

    public static Gen<ImmutableHashSet<ProductGroupModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
