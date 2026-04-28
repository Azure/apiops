using common;
using common.tests;
using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ApiDiagnosticModel : ITestModel<ApiDiagnosticModel>
{
    public required ResourceKey Key { get; init; }

    public required ResourceKey LoggerKey { get; init; }

    public required string Verbosity { get; init; }

    public required bool LogClientIp { get; init; }

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
            from unit in verbosity.Equals(Verbosity, StringComparison.OrdinalIgnoreCase)
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

    public static Gen<ImmutableHashSet<ApiDiagnosticModel>> GenerateSet(IEnumerable<ITestModel> models)
    {
        var modelArray = models.ToImmutableArray();
        var apis = modelArray.OfType<ApiModel>();
        var loggers = modelArray.OfType<LoggerModel>();

        var apiLoggers = from api in apis
                         from logger in loggers
                         select (api, logger);

        return from apiLoggersSubSet in Generator.SubSetOf([.. apiLoggers])
               from diagnostics in Generator.Traverse(apiLoggersSubSet,
                                                      tuple => Generate(tuple.api, tuple.logger))
               select diagnostics.ToImmutableHashSet();
    }

    private static Gen<ApiDiagnosticModel> Generate(ApiModel apiModel, LoggerModel loggerModel) =>
        from verbosity in GenerateVerbosity()
        from logClientIp in Gen.Bool
        select new ApiDiagnosticModel
        {
            Key = new ResourceKey
            {
                Resource = ApiDiagnosticResource.Instance,
                Name = loggerModel.Key.Name,
                Parents = apiModel.Key.AsParentChain()
            },
            LoggerKey = loggerModel.Key,
            Verbosity = verbosity,
            LogClientIp = logClientIp
        };

    private static Gen<string> GenerateVerbosity() =>
        Gen.OneOfConst("information", "verbose", "error");

    public static Gen<ImmutableHashSet<ApiDiagnosticModel>> GenerateUpdates(IEnumerable<ApiDiagnosticModel> apiDiagnosticModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(apiDiagnosticModels, GenerateUpdate)
        select updatedModels.ToImmutableHashSet();

    private static Gen<ApiDiagnosticModel> GenerateUpdate(ApiDiagnosticModel model) =>
        from verbosity in GenerateVerbosity()
        from logClientIp in Gen.Bool
        select model with
        {
            Verbosity = verbosity,
            LogClientIp = logClientIp
        };

    public static Gen<ImmutableHashSet<ApiDiagnosticModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
