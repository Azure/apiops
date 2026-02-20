using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record NamedValueModel : ITestModel<NamedValueModel>
{
    public required string DisplayName { get; init; }

    public required string Value { get; init; }

    public required bool Secret { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = DisplayName,
                ["value"] = Value,
                ["secret"] = Secret
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDisplayName()
               from __ in validateSecret()
               from ___ in validateValue()
               select Unit.Instance;

        Result<Unit> validateDisplayName() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from displayName in properties.GetStringProperty("displayName")
            from unit in displayName == DisplayName
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has displayName '{displayName}' instead of '{DisplayName}'.")
            select unit;

        Result<Unit> validateSecret() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from secret in properties.GetBoolProperty("secret")
            from unit in secret == Secret
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has secret '{secret}' instead of '{Secret}'.")
            select unit;

        Result<Unit> validateValue() =>
            Secret
                // Secret values are not returned by APIM, so we skip validation
                ? Result.Success(Unit.Instance)
                : from properties in dto.GetJsonObjectProperty("properties")
                  from value in properties.GetStringProperty("value")
                  from unit in value == Value
                              ? Result.Success(Unit.Instance)
                              : Error.From($"Resource '{Key}' has value '{value}' instead of '{Value}'.")
                  select unit;
    }

    private static Gen<NamedValueModel> Generator { get; } =
        from name in common.tests.Generator.ResourceName
        from displayName in CommonModule.GenerateDisplayName(name)
        from secret in Gen.Bool
        select new NamedValueModel
        {
            Key = ResourceKey.From(NamedValueResource.Instance, name),
            DisplayName = displayName,
            Value = $"{name}-value",
            Secret = secret
        };

    public static Gen<ImmutableHashSet<NamedValueModel>> GenerateSet() =>
        from models in Generator.HashSetOf(0, 5)
        select models;

    public static Gen<ImmutableHashSet<NamedValueModel>> GenerateUpdates(IEnumerable<NamedValueModel> models) =>
        from updatedModels in
            common.tests.Generator
                        .Traverse(models, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static ImmutableHashSet<NamedValueModel> ToSet(IEnumerable<NamedValueModel> models) =>
        [.. models.DistinctBy(model => model.Key)
                  .DistinctBy(model => model.DisplayName)];

    private static Gen<NamedValueModel> GenerateUpdate(NamedValueModel model) =>
        from displayName in CommonModule.GenerateDisplayName(model.Key.Name, model.DisplayName)
        from secret in Gen.Bool
        select model with
        {
            DisplayName = displayName,
            Value = $"{displayName}-value",
            Secret = secret
        };

    public static Gen<ImmutableHashSet<NamedValueModel>> GenerateNextState(IEnumerable<NamedValueModel> currentModels) =>
        from shuffled in Gen.Shuffle(currentModels.ToArray())
        from keptCount in Gen.Int[0, shuffled.Length]
        let kept = shuffled.Take(keptCount).ToImmutableArray()
        from unchangedCount in Gen.Int[0, kept.Length]
        let unchanged = kept.Take(unchangedCount)
        from changed in common.tests.Generator.Traverse(kept.Skip(unchangedCount), GenerateUpdate)
        from added in GenerateSet()
        select ToSet([.. unchanged, .. changed, .. added]);
}
