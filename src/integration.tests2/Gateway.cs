using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record GatewayModel : ITestModel<GatewayModel>
{
    public required string Description { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["description"] = Description,
                ["locationData"] = new JsonObject
                {
                    ["name"] = "test"
                }
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return validateDescription();

        Result<Unit> validateDescription() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from description in properties.GetStringProperty("description")
            from unit in description == Description
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has description '{description}' instead of '{Description}'.")
            select unit;
    }

    private static Gen<ImmutableHashSet<GatewayModel>> EmptySetGenerator { get; } =
        Gen.Const(ImmutableHashSet<GatewayModel>.Empty);

    // Creating gateways currently fails on the Developer SKU.
    // APIM gives an error message saying the SKU limit has been reached and points to https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#limits---api-management-classic-tiers.
    // Revisit after we have clarity on gateway SKU limits
    public static Gen<ImmutableHashSet<GatewayModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from set in Generate().HashSetOf(0)
        select ToSet(set);

    private static Gen<GatewayModel> Generate() =>
        from name in Generator.ResourceName
        from description in CommonModule.GenerateDescription(name)
        select new GatewayModel
        {
            Key = ResourceKey.From(GatewayResource.Instance, name),
            Description = description
        };

    private static ImmutableHashSet<GatewayModel> ToSet(IEnumerable<GatewayModel> models) =>
        [.. models.DistinctBy(model => model.Key)];

    public static Gen<ImmutableHashSet<GatewayModel>> GenerateUpdates(IEnumerable<GatewayModel> gatewayModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(gatewayModels, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static Gen<GatewayModel> GenerateUpdate(GatewayModel model) =>
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        select model with
        {
            Description = description
        };

    public static Gen<ImmutableHashSet<GatewayModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels)
    {
        var currentModels = previousModels.OfType<GatewayModel>();

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
