using common;
using CsCheck;
using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal abstract record VersioningScheme
{
    internal sealed record Header : VersioningScheme
    {
        public required string HeaderName { get; init; }

        public new static Gen<Header> Generate() =>
            from name in Generator.AlphanumericWord
            select new Header { HeaderName = name };
    }

    internal sealed record Query : VersioningScheme
    {
        public required string QueryName { get; init; }

        public new static Gen<Query> Generate() =>
            from name in Generator.AlphanumericWord
            select new Query { QueryName = name };
    }

    internal sealed record Segment : VersioningScheme
    {
        public new static Gen<Segment> Generate() =>
            Gen.Const(new Segment());
    }

    public static Gen<VersioningScheme> Generate() =>
        Gen.OneOf<VersioningScheme>(Header.Generate(), Query.Generate(), Segment.Generate());
}

internal sealed record VersionSetModel : IDtoTestModel<VersionSetModel>
{
    public required ResourceName Name { get; init; }
    public required VersioningScheme Scheme { get; init; }

    public Option<string> Description { get; init; } = Option.None;

    public static IResourceWithDto AssociatedResource { get; } = VersionSetResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<VersionSetModel> Generate() =>
        from name in Generator.ResourceName
        from scheme in VersioningScheme.Generate()
        from description in GenerateDescription().OptionOf()
        select new VersionSetModel
        {
            Name = name,
            Scheme = scheme,
            Description = description
        };

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<VersionSetModel> GenerateUpdate(VersionSetModel model) =>
        from scheme in VersioningScheme.Generate().OrConst(model.Scheme)
        from description in GenerateDescription().OptionOf().OrConst(model.Description)
        select model with
        {
            Scheme = scheme,
            Description = description
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new VersionSetDto()
        {
            Properties = new VersionSetDto.VersionSetContract
            {
                Description = Description.IfNoneNull(),
                DisplayName = Name.ToString(),
                VersioningScheme = Scheme switch
                {
                    VersioningScheme.Header => "header",
                    VersioningScheme.Query => "query",
                    VersioningScheme.Segment => "segment",
                    _ => throw new InvalidOperationException("Unknown versioning scheme")
                },
                VersionHeaderName = Scheme is VersioningScheme.Header header ? header.HeaderName : null,
                VersionQueryName = Scheme is VersioningScheme.Query query ? query.QueryName : null
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<VersionSetDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<VersionSetDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Description = overrideDto?.Properties?.Description ?? Description,
            VersioningScheme = overrideDto?.Properties?.VersioningScheme ?? Scheme switch
            {
                VersioningScheme.Header => "header",
                VersioningScheme.Query => "query",
                VersioningScheme.Segment => "segment",
                _ => throw new InvalidOperationException("Unknown versioning scheme")
            },
            VersionHeaderName = overrideDto?.Properties?.VersionHeaderName ?? Scheme switch
            {
                VersioningScheme.Header header => header.HeaderName,
                _ => null
            },
            VersionQueryName = overrideDto?.Properties?.VersionQueryName ?? Scheme switch
            {
                VersioningScheme.Query query => query.QueryName,
                _ => null
            }
        };

        var right = new
        {
            Description = jsonDto?.Properties?.Description,
            VersioningScheme = jsonDto?.Properties?.VersioningScheme,
            VersionHeaderName = jsonDto?.Properties?.VersionHeaderName,
            VersionQueryName = jsonDto?.Properties?.VersionQueryName
        };

        return left.Description.FuzzyEquals(right.Description)
               && left.VersioningScheme.FuzzyEquals(right.VersioningScheme)
               && left.VersionHeaderName.FuzzyEquals(right.VersionHeaderName)
               && left.VersionQueryName.FuzzyEquals(right.VersionQueryName);
    }
}
