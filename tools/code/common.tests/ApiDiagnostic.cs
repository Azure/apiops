using CsCheck;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Readers.Interface;
using Nito.Comparers;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace common.tests;

public sealed record ApiDiagnosticSampling
{
    public required string Type { get; init; }
    public required float Percentage { get; init; }

    public static Gen<ApiDiagnosticSampling> Generate() =>
        from type in Gen.Const("fixed")
        from percentage in Gen.Int[0, 100]
        select new ApiDiagnosticSampling
        {
            Type = type,
            Percentage = percentage
        };
}

public record ApiDiagnosticModel
{
    public required ApiDiagnosticName Name { get; init; }
    public required LoggerName LoggerName { get; init; }
    public Option<string> AlwaysLog { get; init; }
    public Option<ApiDiagnosticSampling> Sampling { get; init; }

    public static Gen<ApiDiagnosticModel> Generate() =>
        from loggerType in LoggerType.Generate()
        from loggerName in LoggerModel.GenerateName(loggerType)
        from name in GenerateName(loggerType)
        from alwaysLog in Gen.Const("allErrors").OptionOf()
        from sampling in ApiDiagnosticSampling.Generate().OptionOf()
        select new ApiDiagnosticModel
        {
            Name = name,
            LoggerName = loggerName,
            AlwaysLog = alwaysLog,
            Sampling = sampling
        };

    public static Gen<ApiDiagnosticName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select ApiDiagnosticName.From(name);

    private static Gen<ApiDiagnosticName> GenerateName(LoggerType loggerType) =>
        loggerType switch
        {
            LoggerType.AzureMonitor => Gen.Const(ApiDiagnosticName.From("azuremonitor")),
            _ => GenerateName()
        };

    public static Gen<FrozenSet<ApiDiagnosticModel>> GenerateSet() =>
        Generate().FrozenSetOf(0, 20, Comparer);

    public static Gen<FrozenSet<ApiDiagnosticModel>> GenerateSet(FrozenSet<ApiDiagnosticModel> originalSet, ICollection<LoggerModel> loggers)
    {
        if (loggers.Count == 0)
        {
            Gen.Const(Enumerable.Empty<ApiDiagnosticModel>().ToFrozenSet(Comparer));
        }

        var loggersArray = loggers.ToArray();

        return from updates in originalSet.Select(diagnostic => from logger in Gen.OneOfConst(loggersArray)
                                                                select diagnostic with
                                                                {
                                                                    // Diagnostic name must be "azuremonitor" if the logger type is AzureMonitor
                                                                    Name = logger.Type is LoggerType.AzureMonitor ? ApiDiagnosticName.From("azuremonitor") : diagnostic.Name,
                                                                    LoggerName = logger.Name
                                                                })
                                          .SequenceToFrozenSet(originalSet.Comparer)
               select updates;
    }

    private static IEqualityComparer<ApiDiagnosticModel> Comparer { get; } =
        EqualityComparerBuilder.For<ApiDiagnosticModel>()
                               .EquateBy(x => x.Name);
}
