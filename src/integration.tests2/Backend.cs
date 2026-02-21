using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record BackendModel : ITestModel<BackendModel>
{
    public required string Description { get; init; }

    public required string Url { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["description"] = Description,
                ["url"] = Url,
                ["protocol"] = "http"
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDescription()
               from __ in validateUrl()
               select Unit.Instance;

        Result<Unit> validateDescription() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from description in properties.GetStringProperty("description")
            from unit in description == Description
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has description '{description}' instead of '{Description}'.")
            select unit;

        Result<Unit> validateUrl() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from url in properties.GetStringProperty("url")
            from unit in url == Url
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has url '{url}' instead of '{Url}'.")
            select unit;
    }

    private static Gen<BackendModel> Generator { get; } =
        from name in common.tests.Generator.ResourceName
        from description in CommonModule.GenerateDescription(name)
        select new BackendModel
        {
            Key = ResourceKey.From(BackendResource.Instance, name),
            Description = description,
            Url = $"https://{description}.example.com"
        };

    public static Gen<ImmutableHashSet<BackendModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Generator.HashSetOf(0, 5);

    public static Gen<ImmutableHashSet<BackendModel>> GenerateUpdates(IEnumerable<BackendModel> models) =>
        from updatedModels in
            common.tests.Generator
                        .Traverse(models, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static ImmutableHashSet<BackendModel> ToSet(IEnumerable<BackendModel> models) =>
        [.. models.DistinctBy(model => model.Key)];

    private static Gen<BackendModel> GenerateUpdate(BackendModel model) =>
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        select model with
        {
            Description = description,
            Url = $"https://{description}.example.com"
        };

    public static Gen<ImmutableHashSet<BackendModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<BackendModel>();

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
