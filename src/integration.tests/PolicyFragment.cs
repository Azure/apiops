using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record PolicyFragmentModel : IPolicyResourceTestModel<PolicyFragmentModel>
{
    public required ResourceName Name { get; init; }
    public required string Content { get; init; }

    public Option<string> Description { get; init; } = Option.None;

    public static IPolicyResource AssociatedResource { get; } = PolicyFragmentResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<PolicyFragmentModel> Generate() =>
        from name in Generator.ResourceName
        from content in GenerateContent()
        from description in GenerateDescription().OptionOf()
        select new PolicyFragmentModel
        {
            Name = name,
            Content = content,
            Description = description
        };

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<string> GenerateContent() =>
        from policy in Gen.OneOf(Generator.IpFilterPolicySnippet,
                                 Generator.SetHeaderPolicySnippet)
        select $"""
                <fragment>
                    {policy}
                </fragment>
                """;


    private static Gen<PolicyFragmentModel> GenerateUpdate(PolicyFragmentModel model) =>
        from content in GenerateContent().OrConst(model.Content)
        from description in GenerateDescription().OptionOf().OrConst(model.Description)
        select model with
        {
            Content = content,
            Description = description
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new PolicyDto()
        {
            Properties = new PolicyDto.PolicyContract
            {
                Description = Description.IfNoneNull(),
                Format = "rawxml",
                Value = Content
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<PolicyDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<PolicyDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Description = overrideDto?.Properties?.Description ?? Description,
            Contents = overrideDto?.Properties?.Value ?? Content
        };

        var right = new
        {
            Description = jsonDto?.Properties?.Description,
            Contents = jsonDto?.Properties?.Value
        };

        return left.Description.FuzzyEquals(right.Description)
               && left.Contents.FuzzyEqualsPolicy(right.Contents);
    }
}