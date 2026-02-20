using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record TagModel : ITestModel<TagModel>
{
    public required string DisplayName { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = DisplayName,
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return validateDisplayName();

        Result<Unit> validateDisplayName() =>
         from properties in dto.GetJsonObjectProperty("properties")
         from displayName in properties.GetStringProperty("displayName")
         from unit in displayName == DisplayName
                     ? Result.Success(Unit.Instance)
                     : Error.From($"Resource '{Key}' has displayName '{displayName}' instead of '{DisplayName}'.")
         select unit;
    }

    private static Gen<TagModel> Generator { get; } =
        from name in common.tests.Generator.ResourceName
        from displayName in CommonModule.GenerateDisplayName(name)
        select new TagModel
        {
            Key = ResourceKey.From(TagResource.Instance, name),
            DisplayName = displayName
        };

    public static Gen<ImmutableHashSet<TagModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Generator.HashSetOf(0, 5);

    public static Gen<ImmutableHashSet<TagModel>> GenerateUpdates(IEnumerable<TagModel> models) =>
        from updatedModels in
            common.tests.Generator
                        .Traverse(models, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static ImmutableHashSet<TagModel> ToSet(IEnumerable<TagModel> models) =>
        [.. models.DistinctBy(model => model.Key)
                  .DistinctBy(model => model.DisplayName)];

    private static Gen<TagModel> GenerateUpdate(TagModel model) =>
        from displayName in CommonModule.GenerateDisplayName(model.Key.Name, model.DisplayName)
        select model with
        {
            DisplayName = displayName
        };

    public static Gen<ImmutableHashSet<TagModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<TagModel>();

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
