using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record LoggerModel : ITestModel<LoggerModel>
{
    private static readonly ResourceName azureMonitorName =
        ResourceName.From("azuremonitor").IfErrorThrow();

    public required string Description { get; init; }

    public required bool IsBuffered { get; init; }

    public ResourceKey Key { get; init; } = new ResourceKey
    {
        Resource = LoggerResource.Instance,
        Name = azureMonitorName,
        Parents = ParentChain.Empty
    };

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["loggerType"] = "azureMonitor",
                ["description"] = Description,
                ["isBuffered"] = IsBuffered
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateDescription()
               from __ in validateIsBuffered()
               select Unit.Instance;

        Result<Unit> validateDescription() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from description in properties.GetStringProperty("description")
            from unit in description == Description
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has description '{description}' instead of '{Description}'.")
            select unit;

        Result<Unit> validateIsBuffered() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from isBuffered in properties.GetBoolProperty("isBuffered")
            from unit in isBuffered == IsBuffered
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has isBuffered '{isBuffered}' instead of '{IsBuffered}'.")
            select unit;
    }

    private static Gen<LoggerModel> Generator { get; } =
        from description in CommonModule.GenerateDescription(azureMonitorName)
        from isBuffered in Gen.Bool
        select new LoggerModel
        {
            Description = description,
            IsBuffered = isBuffered
        };

    private static Gen<ImmutableHashSet<LoggerModel>> EmptySetGen { get; } =
        Gen.Const(ImmutableHashSet<LoggerModel>.Empty);

    private static Gen<ImmutableHashSet<LoggerModel>> SingletonSetGen { get; } =
        from model in Generator
        select ImmutableHashSet.Create(model);

    public static Gen<ImmutableHashSet<LoggerModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Gen.Frequency((1, EmptySetGen), (5, SingletonSetGen));

    public static Gen<ImmutableHashSet<LoggerModel>> GenerateUpdates(IEnumerable<LoggerModel> models) =>
        from updatedModels in common.tests.Generator.Traverse(models, GenerateUpdate)
        select updatedModels.ToImmutableHashSet();

    private static Gen<LoggerModel> GenerateUpdate(LoggerModel model) =>
        from description in CommonModule.GenerateDescription(model.Key.Name, model.Description)
        from isBuffered in Gen.Bool
        select model with
        {
            Description = description,
            IsBuffered = isBuffered
        };

    public static Gen<ImmutableHashSet<LoggerModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        Gen.Frequency((1, EmptySetGen), (5, SingletonSetGen));
}
