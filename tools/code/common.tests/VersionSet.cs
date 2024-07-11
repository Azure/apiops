using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public abstract record VersioningScheme
{
    public sealed record Header : VersioningScheme
    {
        public required string HeaderName { get; init; }

        public new static Gen<Header> Generate() =>
            from name in GenerateHeaderName()
            select new Header { HeaderName = name };

        public static Gen<string> GenerateHeaderName() =>
            Generator.AlphaNumericStringBetween(10, 20);
    }

    public sealed record Query : VersioningScheme
    {
        public required string QueryName { get; init; }

        public new static Gen<Query> Generate() =>
            from name in GenerateQueryName()
            select new Query { QueryName = name };

        public static Gen<string> GenerateQueryName() =>
            Generator.AlphaNumericStringBetween(10, 20);
    }

    public sealed record Segment : VersioningScheme
    {
        public new static Gen<Segment> Generate() =>
            Gen.Const(new Segment());
    }

    public static Gen<VersioningScheme> Generate() =>
        Gen.OneOf<VersioningScheme>(Header.Generate(), Query.Generate(), Segment.Generate());
}

public sealed record VersionSetModel
{
    public required VersionSetName Name { get; init; }
    public required string DisplayName { get; init; }
    public required VersioningScheme Scheme { get; init; }
    public Option<string> Description { get; init; }

    public static Gen<VersionSetModel> Generate() =>
        from name in GenerateName()
        from displayName in GenerateDisplayName()
        from scheme in VersioningScheme.Generate()
        from description in GenerateDescription().OptionOf()
        select new VersionSetModel
        {
            Name = name,
            DisplayName = displayName,
            Scheme = scheme,
            Description = description
        };

    public static Gen<VersionSetName> GenerateName() =>
        from name in Generator.AlphaNumericStringBetween(10, 20)
        select VersionSetName.From(name);

    public static Gen<string> GenerateDisplayName() =>
        Generator.AlphaNumericStringBetween(10, 20);

    public static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    public static Gen<FrozenSet<VersionSetModel>> GenerateSet() =>
        Generate().FrozenSetOf(x => x.Name, 0, 10)
                  .DistinctBy(x => x.DisplayName);
}