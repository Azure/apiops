using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record VersionSetModel : ITestModel<VersionSetModel>
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
                ["description"] = Description,
                ["versioningScheme"] = "Segment"
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

    public static Gen<ImmutableHashSet<VersionSetModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from set in Generate().HashSetOf(0, 5)
        select ToSet(set);

    private static Gen<VersionSetModel> Generate() =>
        from name in Generator.ResourceName
        from displayName in CommonModule.GenerateDisplayName(name)
        from description in CommonModule.GenerateDescription(name)
        select new VersionSetModel
        {
            Key = ResourceKey.From(VersionSetResource.Instance, name),
            DisplayName = displayName,
            Description = description
        };

    private static ImmutableHashSet<VersionSetModel> ToSet(IEnumerable<VersionSetModel> models) =>
        [.. models.DistinctBy(model => model.Key)
                  .DistinctBy(model => model.DisplayName)];

    public static Gen<ImmutableHashSet<VersionSetModel>> GenerateUpdates(IEnumerable<VersionSetModel> versionSetModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(versionSetModels, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static Gen<VersionSetModel> GenerateUpdate(VersionSetModel model) =>
        from displayName in CommonModule.GenerateDisplayName(model.Key.Name, model.DisplayName)
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        select model with
        {
            DisplayName = displayName,
            Description = description
        };

    public static Gen<ImmutableHashSet<VersionSetModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<VersionSetModel>();

        return from shuffled in Gen.Shuffle(currentModels.ToArray())
               from keptCount in Gen.Int[0, shuffled.Length]
               let kept = shuffled.Take(keptCount).ToImmutableArray()
               from unchangedCount in Gen.Int[0, kept.Length]
               let unchanged = kept.Take(unchangedCount)
               from changed in GenerateUpdates(kept.Skip(unchangedCount), accumulatedNextModels)
               from added in GenerateSet(accumulatedNextModels)
               select ToSet([.. unchanged, .. changed, .. added]);
    }
}
