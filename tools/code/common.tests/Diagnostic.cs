using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record DiagnosticSampling
{
    public required string Type { get; init; }
    public required float Percentage { get; init; }

    public static Gen<DiagnosticSampling> Generate() =>
        from type in Gen.Const("fixed")
        from percentage in Gen.Int[0, 100]
        select new DiagnosticSampling
        {
            Type = type,
            Percentage = percentage
        };
}

public sealed record DiagnosticLargeLanguageModelMessages
{
    public required string Messages { get; init; }
    public required int MaxSizeInBytes { get; init; }

    public static Gen<DiagnosticLargeLanguageModelMessages> Generate() =>
        from messages in Gen.OneOfConst("all", "errors")
        from maxSize in Gen.Int[0, 65536]
        select new DiagnosticLargeLanguageModelMessages
        {
            Messages = messages,
            MaxSizeInBytes = maxSize
        };
}

public sealed record DiagnosticLargeLanguageModel
{
    public Option<string> Logs { get; init; }
    public Option<DiagnosticLargeLanguageModelMessages> Requests { get; init; }
    public Option<DiagnosticLargeLanguageModelMessages> Responses { get; init; }

    public static Gen<DiagnosticLargeLanguageModel> Generate() =>
        from logs in Gen.OneOfConst("enabled", "disabled").OptionOf()
        from requests in DiagnosticLargeLanguageModelMessages.Generate().OptionOf()
        from responses in DiagnosticLargeLanguageModelMessages.Generate().OptionOf()
        select new DiagnosticLargeLanguageModel
        {
            Logs = logs,
            Requests = requests,
            Responses = responses
        };
}

public record DiagnosticModel
{
    public required DiagnosticName Name { get; init; }
    public required LoggerName LoggerName { get; init; }
    public Option<string> AlwaysLog { get; init; }
    public Option<DiagnosticSampling> Sampling { get; init; }
    public Option<DiagnosticLargeLanguageModel> LargeLanguageModel { get; init; }

    public static Gen<DiagnosticModel> Generate() =>
        from name in GenerateName()
        from loggerType in LoggerType.Generate()
        from loggerName in LoggerModel.GenerateName(loggerType)
        from alwaysLog in Gen.Const("allErrors").OptionOf()
        from sampling in DiagnosticSampling.Generate().OptionOf()
        from largeLanguageModel in DiagnosticLargeLanguageModel.Generate().OptionOf()
        select new DiagnosticModel
        {
            Name = name,
            LoggerName = loggerName,
            AlwaysLog = alwaysLog,
            Sampling = sampling,
            LargeLanguageModel = largeLanguageModel
        };

    public static Gen<DiagnosticName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select DiagnosticName.From(name);

    public static Gen<FrozenSet<DiagnosticModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 20);
}
