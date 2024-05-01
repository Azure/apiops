using CsCheck;
using LanguageExt;
using System;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record BackendModel
{
    public required BackendName Name { get; init; }
    public string Protocol { get; } = "http";
    public required Uri Url { get; init; }
    public Option<string> Description { get; init; }

    public static Gen<BackendModel> Generate() =>
        from name in GenerateName()
        from description in GenerateDescription().OptionOf()
        from url in Generator.AbsoluteUri
        select new BackendModel
        {
            Name = name,
            Url = url,
            Description = description
        };

    public static Gen<BackendName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select BackendName.From(name);

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<FrozenSet<BackendModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10);
}