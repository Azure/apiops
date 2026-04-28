using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record DiagnosticModel : ITestModel<DiagnosticModel>
{
    public required string Verbosity { get; init; }

    public required bool LogClientIp { get; init; }

    public required ResourceKey LoggerKey { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["loggerId"] = $"/{LoggerKey}",
                ["verbosity"] = Verbosity,
                ["logClientIp"] = LogClientIp
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return from _ in validateVerbosity()
               from __ in validateLogClientIp()
               select Unit.Instance;

        Result<Unit> validateVerbosity() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from verbosity in properties.GetStringProperty("verbosity")
            from unit in verbosity == Verbosity
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has verbosity '{verbosity}' instead of '{Verbosity}'.")
            select unit;

        Result<Unit> validateLogClientIp() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from logClientIp in properties.GetBoolProperty("logClientIp")
            from unit in logClientIp == LogClientIp
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has logClientIp '{logClientIp}' instead of '{LogClientIp}'.")
            select unit;
    }

    public static Gen<ImmutableHashSet<DiagnosticModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from loggerModels in Generator.SubSetOf([.. models.OfType<LoggerModel>()])
        from diagnosticModels in Generator.Traverse(loggerModels, Generate)
        select ToSet(diagnosticModels);

    private static Gen<DiagnosticModel> Generate(LoggerModel logger) =>
        from verbosity in GenerateVerbosity()
        from logClientIp in Gen.Bool
        select new DiagnosticModel
        {
            Key = ResourceKey.From(DiagnosticResource.Instance, logger.Key.Name),
            LoggerKey = logger.Key,
            Verbosity = verbosity,
            LogClientIp = logClientIp
        };

    private static ImmutableHashSet<DiagnosticModel> ToSet(IEnumerable<DiagnosticModel> models) =>
        [.. models.DistinctBy(model => model.Key)];

    private static Gen<string> GenerateVerbosity() =>
        Gen.OneOfConst("information", "verbose", "error");

    public static Gen<ImmutableHashSet<DiagnosticModel>> GenerateUpdates(IEnumerable<DiagnosticModel> diagnosticModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(diagnosticModels, GenerateUpdate)
        let updatedSet = ToSet(updatedModels)
        where updatedSet.Count == updatedModels.Length
        select updatedSet;

    private static Gen<DiagnosticModel> GenerateUpdate(DiagnosticModel model) =>
        from verbosity in GenerateVerbosity()
        from logClientIp in Gen.Bool
        select model with
        {
            Verbosity = verbosity,
            LogClientIp = logClientIp
        };

    public static Gen<ImmutableHashSet<DiagnosticModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
