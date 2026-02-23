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

    private static Gen<GatewayModel> Generator { get; } =
        from name in common.tests.Generator.ResourceName
        from description in CommonModule.GenerateDescription(name)
        select new GatewayModel
        {
            Key = ResourceKey.From(GatewayResource.Instance, name),
            Description = description
        };

    private static Gen<ImmutableHashSet<GatewayModel>> EmptySetGenerator { get; } =
        Gen.Const(ImmutableHashSet<GatewayModel>.Empty);

    // Creating gateways currently fails on the Developer SKU.
    // APIM gives an error message saying the SKU limit has been reached and points to https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#limits---api-management-classic-tiers.
    // Revisit after we have clarity on gateway SKU limits
    public static Gen<ImmutableHashSet<GatewayModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        EmptySetGenerator;

    public static Gen<ImmutableHashSet<GatewayModel>> GenerateUpdates(IEnumerable<GatewayModel> models) =>
        from updatedModels in common.tests.Generator.Traverse(models, GenerateUpdate)
        select updatedModels.ToImmutableHashSet();

    private static Gen<GatewayModel> GenerateUpdate(GatewayModel model) =>
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        select model with
        {
            Description = description
        };

    public static Gen<ImmutableHashSet<GatewayModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        EmptySetGenerator;
}
