using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public abstract record LoggerType
{
    public sealed record AzureMonitor : LoggerType;

    public sealed record EventHub : LoggerType
    {
        public required string Name { get; init; }
        public required string ResourceId { get; init; }
        public required NamedValueName ConnectionStringNamedValue { get; init; }
    }

    public sealed record ApplicationInsights : LoggerType
    {
        public required NamedValueName InstrumentationKeyNamedValue { get; init; }
        public required string ResourceId { get; init; }
    }

    public static Gen<LoggerType> Generate() =>
        Gen.Const<LoggerType>(new AzureMonitor());
}

public record LoggerModel
{
    public required LoggerName Name { get; init; }
    public required LoggerType Type { get; init; }
    public Option<string> Description { get; init; } = Option<string>.None;
    public bool IsBuffered { get; init; } = true;

    public static Gen<LoggerModel> Generate() =>
        from type in LoggerType.Generate()
        from name in GenerateName(type)
        from description in GenerateDescription().OptionOf()
        from isBuffered in Gen.Bool
        select new LoggerModel
        {
            Name = name,
            Type = type,
            Description = description,
            IsBuffered = isBuffered
        };

    public static Gen<LoggerName> GenerateName(LoggerType type) =>
        type is LoggerType.AzureMonitor
            ? Gen.Const(LoggerName.From("azuremonitor"))
            : from name in Generator.AlphaNumericStringBetween(10, 20)
              select LoggerName.From(name);

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Sentence();

    public static Gen<FrozenSet<LoggerModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 20);
}
