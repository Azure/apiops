using common;
using CsCheck;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ProductModel : IDtoTestModel<ProductModel>
{
    public required ResourceName Name { get; init; }
    public required string State { get; init; }
    public required string Description { get; init; }
    public required bool SubscriptionRequired { get; init; }
    public required Option<string> Terms { get; init; }

    public static IResourceWithDto AssociatedResource { get; } = ProductResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<ProductModel> Generate() =>
        from name in Generator.ResourceName
        from state in GenerateState()
        from description in GenerateDescription()
        from terms in GenerateTerms().OptionOf()
        from subscriptionRequired in Gen.Bool
        select new ProductModel
        {
            Name = name,
            State = state,
            Description = description,
            Terms = terms,
            SubscriptionRequired = subscriptionRequired
        };

    private static Gen<string> GenerateState() =>
        Gen.OneOfConst("published", "notPublished");

    private static Gen<string> GenerateDescription() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<string> GenerateTerms() =>
        from lorem in Generator.Lorem
        select lorem.Paragraph();

    private static Gen<ProductModel> GenerateUpdate(ProductModel model) =>
        from state in GenerateState().OrConst(model.State)
        from description in GenerateDescription().OrConst(model.Description)
        from terms in GenerateTerms().OptionOf().OrConst(model.Terms)
        from subscriptionRequired in Gen.Bool.OrConst(model.SubscriptionRequired)
        select model with
        {
            State = state,
            Description = description,
            Terms = terms,
            SubscriptionRequired = subscriptionRequired
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new ProductDto()
        {
            Properties = new ProductDto.ProductContract
            {
                DisplayName = Name.ToString(),
                State = State,
                Description = Description,
                Terms = Terms.IfNoneNull(),
                SubscriptionRequired = SubscriptionRequired
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<ProductDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<ProductDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Description = overrideDto?.Properties?.Description ?? Description,
            Terms = overrideDto?.Properties?.Terms ?? Terms.IfNoneNull(),
            SubscriptionRequired = overrideDto?.Properties?.SubscriptionRequired ?? SubscriptionRequired
        };

        var right = new
        {
            Description = jsonDto?.Properties?.Description,
            Terms = jsonDto?.Properties?.Terms,
            SubscriptionRequired = jsonDto?.Properties?.SubscriptionRequired
        };

        return left.Description.FuzzyEquals(right.Description)
               && left.Terms.FuzzyEquals(right.Terms)
               && left.SubscriptionRequired.FuzzyEquals(right.SubscriptionRequired);
    }
}