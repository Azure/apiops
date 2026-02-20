using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ProductModel : ITestModel<ProductModel>
{
    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = DisplayName,
                ["description"] = Description
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDisplayName()
               from __ in validateDescription()
               select Unit.Instance;

        Result<Unit> validateDisplayName() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from displayName in properties.GetStringProperty("displayName")
            from unit in displayName == DisplayName
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has displayName '{displayName}' instead of '{DisplayName}'.")
            select unit;

        Result<Unit> validateDescription() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from description in properties.GetStringProperty("description")
            from unit in description == Description
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has description '{description}' instead of '{Description}'.")
            select unit;
    }

    private static Gen<ProductModel> Generator { get; } =
        from name in common.tests.Generator.ResourceName
        from displayName in CommonModule.GenerateDisplayName(name)
        from description in CommonModule.GenerateDescription(name)
        select new ProductModel
        {
            Key = ResourceKey.From(ProductResource.Instance, name),
            DisplayName = displayName,
            Description = description
        };

    public static Gen<ImmutableHashSet<ProductModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Generator.HashSetOf(0, 5);

    public static Gen<ImmutableHashSet<ProductModel>> GenerateUpdates(IEnumerable<ProductModel> models) =>
        from updatedModels in
            common.tests.Generator
                        .Traverse(models, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static ImmutableHashSet<ProductModel> ToSet(IEnumerable<ProductModel> models) =>
        [.. models.DistinctBy(model => model.Key)
                  .DistinctBy(model => model.DisplayName)];

    private static Gen<ProductModel> GenerateUpdate(ProductModel model) =>
        from displayName in CommonModule.GenerateDisplayName(model.Key.Name, model.DisplayName)
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        select model with
        {
            DisplayName = displayName,
            Description = description
        };

    public static Gen<ImmutableHashSet<ProductModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<ProductModel>();

        return from shuffled in Gen.Shuffle(currentModels.ToArray())
               from keptCount in Gen.Int[0, shuffled.Length]
               let kept = shuffled.Take(keptCount).ToImmutableArray()
               from unchangedCount in Gen.Int[0, kept.Length]
               let unchanged = kept.Take(unchangedCount)
               from changed in common.tests.Generator.Traverse(kept.Skip(unchangedCount), GenerateUpdate)
               from added in GenerateSet(accumulatedNextModels)
               select ToSet([.. unchanged, .. changed, .. added]);
    }
}
