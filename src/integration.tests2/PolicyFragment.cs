using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record PolicyFragmentModel : ITestModel<PolicyFragmentModel>
{
    public required string Description { get; init; }

    public required string Content { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["description"] = Description,
                ["format"] = "rawxml",
                ["value"] = Content
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDescription()
               from __ in validateContent()
               select Unit.Instance;

        Result<Unit> validateDescription() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from description in properties.GetStringProperty("description")
            from unit in description == Description
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has description '{description}' instead of '{Description}'.")
            select unit;

        Result<Unit> validateContent() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from value in properties.GetStringProperty("value")
            from unit in PolicyModule.FuzzyEquals(value, Content)
                            ? Result.Success(Unit.Instance)
                            : Error.From($"Resource '{Key}' has policy content that doesn't match expected content.")
            select unit;
    }

    private static Gen<string> GenerateContent(IEnumerable<ITestModel> models)
    {
        var namedValues = models.OfType<NamedValueModel>();

        return from setVariableSnippet in PolicyModule.GenerateSetVariableSnippet(namedValues)
               select $"""
                       <fragment>
                           {setVariableSnippet}
                       </fragment>
                       """;
    }

    private static Gen<PolicyFragmentModel> Generate(IEnumerable<ITestModel> models) =>
        from name in Generator.ResourceName
        from description in CommonModule.GenerateDescription(name)
        from content in GenerateContent(models)
        select new PolicyFragmentModel
        {
            Key = ResourceKey.From(PolicyFragmentResource.Instance, name),
            Description = description,
            Content = content
        };

    public static Gen<ImmutableHashSet<PolicyFragmentModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Generate(models).HashSetOf(0, 5);

    public static Gen<ImmutableHashSet<PolicyFragmentModel>> GenerateUpdates(IEnumerable<PolicyFragmentModel> models) =>
        from updatedModels in Generator.Traverse(models, model => GenerateUpdate(model, models))
        select updatedModels.ToImmutableHashSet();

    private static Gen<PolicyFragmentModel> GenerateUpdate(PolicyFragmentModel model, IEnumerable<ITestModel> allModels) =>
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        from content in GenerateContent(allModels)
        select model with
        {
            Description = description,
            Content = content
        };

    public static Gen<ImmutableHashSet<PolicyFragmentModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<PolicyFragmentModel>();

        return from shuffled in Gen.Shuffle(currentModels.ToArray())
               from keptCount in Gen.Int[0, shuffled.Length]
               let kept = shuffled.Take(keptCount).ToImmutableArray()
               from unchangedCount in Gen.Int[0, kept.Length]
               let unchanged = kept.Take(unchangedCount)
               from changed in Generator.Traverse(kept.Skip(unchangedCount), model => GenerateUpdate(model, accumulatedNextModels))
               from added in GenerateSet(accumulatedNextModels)
               select ImmutableHashSet.CreateRange([.. unchanged, .. changed, .. added]);
    }
}
