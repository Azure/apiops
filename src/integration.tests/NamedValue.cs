using common;
using CsCheck;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record NamedValueModel : IDtoTestModel<NamedValueModel>
{
    public required ResourceName Name { get; init; }
    public required string Value { get; init; }
    public required ImmutableHashSet<string> Tags { get; init; }
    public required bool IsSecret { get; init; }

    public static IResourceWithDto AssociatedResource { get; } = NamedValueResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes<NamedValueModel>(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<NamedValueModel> Generate() =>
        from name in Generator.ResourceName
        from value in GenerateValue()
        from tags in GenerateTags()
        from isSecret in Gen.Bool
        select new NamedValueModel
        {
            Name = name,
            Value = value,
            Tags = tags,
            IsSecret = isSecret
        };

    private static Gen<string> GenerateValue() =>
        from value in Gen.String.AlphaNumeric
        where value.Length is >= 1 and <= 4096
        select value;

    private static Gen<ImmutableHashSet<string>> GenerateTags()
    {
        var tagGenerator = from tag in Gen.String.AlphaNumeric
                           where tag.Length > 0
                           select tag[..Math.Min(tag.Length, 20)];

        return tagGenerator.HashSetOf();
    }

    private static Gen<NamedValueModel> GenerateUpdate(NamedValueModel model) =>
        from value in GenerateValue().OrConst(model.Value)
        from tags in GenerateTags().OrConst(model.Tags)
        from isSecret in Gen.Bool.OrConst(model.IsSecret)
        select model with
        {
            Value = value,
            Tags = tags,
            IsSecret = isSecret
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new NamedValueDto()
        {
            Properties = new NamedValueDto.NamedValueContract
            {
                DisplayName = Name.ToString(),
                Value = Value,
                Tags = [.. Tags],
                Secret = IsSecret
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<NamedValueDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<NamedValueDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            IsSecret = overrideDto?.Properties?.Secret ?? IsSecret,
            Tags = overrideDto?.Properties?.Tags?.ToImmutableHashSet() ?? Tags,
            Value = overrideDto?.Properties?.Secret ?? IsSecret // Skip value comparison for secrets
                    ? string.Empty
                    : overrideDto?.Properties?.Value ?? Value
        };

        var right = new
        {
            IsSecret = jsonDto?.Properties?.Secret ?? false,
            Tags = jsonDto?.Properties?.Tags?.ToImmutableHashSet() ?? [],
            Value = jsonDto?.Properties?.Value ?? string.Empty
        };

        return left.IsSecret.FuzzyEquals(right.IsSecret)
               && left.Tags.FuzzyEquals(right.Tags)
               && left.Value.FuzzyEquals(right.Value);
    }
}