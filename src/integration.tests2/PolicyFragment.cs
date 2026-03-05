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

    public static Gen<ImmutableHashSet<PolicyFragmentModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from set in Generate(models).HashSetOf(0, 5)
        select ToSet(set);

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

    private static ImmutableHashSet<PolicyFragmentModel> ToSet(IEnumerable<PolicyFragmentModel> models) =>
        [.. models.DistinctBy(model => model.Key)];

    public static Gen<ImmutableHashSet<PolicyFragmentModel>> GenerateUpdates(IEnumerable<PolicyFragmentModel> policyFragmentModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(policyFragmentModels,
                                                 model => GenerateUpdate(model, allModels))
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

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

        return from changed in
                   from modelsToUpdate in Generator.SubSetOf([.. currentModels])
                   from changed in Generator.Traverse(modelsToUpdate, model => GenerateUpdate(model, accumulatedNextModels))
                   select changed
               from added in GenerateSet(accumulatedNextModels)
               select ImmutableHashSet.CreateRange([.. changed, .. added]);
    }
}
