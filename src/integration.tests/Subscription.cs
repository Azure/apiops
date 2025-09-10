using common;
using CsCheck;
using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal abstract record SubscriptionScope
{
    internal sealed record Product : SubscriptionScope
    {
        public required ResourceName Name { get; init; }
    }

    internal sealed record Api : SubscriptionScope
    {
        public required ResourceName Name { get; init; }
    }

    internal sealed record AllApis : SubscriptionScope
    {
        public static AllApis Instance { get; } = new();
    }
}

internal sealed record SubscriptionModel : IResourceWithReferenceTestModel<SubscriptionModel>
{
    public required ResourceName Name { get; init; }
    public required SubscriptionScope Scope { get; init; }
    public required string State { get; init; }
    public required bool AllowTracing { get; init; }

    public static IResourceWithReference AssociatedResource { get; } = SubscriptionResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from predecessors in Generator.GeneratePredecessors<SubscriptionModel>(baseline)
                                                         .IfNone(() => Gen.Const(ModelNodeSet.Empty))
                           let product = predecessors.Pick<ProductResource>()
                           let productName = product.Map(node => node.Model.Name)
                           let api = predecessors.Pick<ApiResource>()
                           let apiName = api.Map(node => node.Model.Name)
                           from model in Generate(productName, apiName)
                           let updatedPredecessors = model.Scope switch
                           {
                               SubscriptionScope.Product productScope => ModelNodeSet.From([product.IfNone(() => throw new InvalidOperationException($"Product {productScope.Name} must exist."))]),
                               SubscriptionScope.Api apiScope => ModelNodeSet.From([api.IfNone(() => throw new InvalidOperationException($"API {apiScope.Name} must exist."))]),
                               SubscriptionScope.AllApis => ModelNodeSet.Empty,
                               _ => throw new InvalidOperationException($"Scope {model.Scope} is not supported.")
                           }
                           select (model, updatedPredecessors);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<SubscriptionModel> Generate(Option<ResourceName> productName, Option<ResourceName> apiName) =>
        from name in Generator.ResourceName
        let scope = (productName.IfNoneNull(), apiName.IfNoneNull()) switch
        {
            (null, null) => (SubscriptionScope)SubscriptionScope.AllApis.Instance,
            (var productName, null) => new SubscriptionScope.Product { Name = productName },
            (null, var apiName) => new SubscriptionScope.Api { Name = apiName },
            // If both are provided, we default to the product scope.
            (var productName, var apiName) => new SubscriptionScope.Product { Name = productName }
        }
        from state in GenerateState()
        from allowTracing in Gen.Bool
        select new SubscriptionModel
        {
            Name = name,
            Scope = scope,
            State = state,
            AllowTracing = allowTracing,
        };

    private static Gen<string> GenerateState() =>
        Gen.OneOfConst("active", "suspended");

    private static Gen<SubscriptionModel> GenerateUpdate(SubscriptionModel model) =>
        from state in GenerateState().OrConst(model.State)
            // We can only move to "expired" from an "active" state
        where model.State is "active" || state is not "expired"
        // We can only move to "submitted" from a "rejected" state
        where model.State is "rejected" || state is not "submitted"
        // We can only move to "suspended" from an "active" or "submitted" state
        where model.State is "active" or "submitted" || state is not "suspended"
        from allowTracing in Gen.Bool.OrConst(model.AllowTracing)
        select model with
        {
            State = state,
            AllowTracing = allowTracing
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new SubscriptionDto()
        {
            Properties = new SubscriptionDto.SubscriptionContract
            {
                DisplayName = Name.ToString(),
                AllowTracing = AllowTracing,
                State = State,
                Scope = Scope switch
                {
                    SubscriptionScope.Product product => predecessors.First(node => node.Model.AssociatedResource is ProductResource
                                                                                    && node.Model.Name == product.Name)
                                                                     .ToResourceId(),
                    SubscriptionScope.Api api => predecessors.First(node => node.Model.AssociatedResource is ApiResource
                                                                            && node.Model.Name == api.Name)
                                                             .ToResourceId(),
                    SubscriptionScope.AllApis => "/apis",
                    var scope => throw new InvalidOperationException($"Scope {scope} is not supported.")
                }
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<SubscriptionDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<SubscriptionDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            State = overrideDto?.Properties?.State ?? State,
            AllowTracing = overrideDto?.Properties?.AllowTracing ?? AllowTracing,
            Scope = overrideDto?.Properties?.Scope?.Split('/').LastOrDefault() ?? Scope switch
            {
                SubscriptionScope.Product product => product.Name.ToString(),
                SubscriptionScope.Api api => api.Name.ToString(),
                SubscriptionScope.AllApis => "apis",
                _ => string.Empty
            }
        };

        var right = new
        {
            State = jsonDto?.Properties?.State,
            AllowTracing = jsonDto?.Properties?.AllowTracing,
            Scope = jsonDto?.Properties?.Scope?.Split('/')?.LastOrDefault()
        };

        return left.State.FuzzyEquals(right.State)
               && left.AllowTracing.FuzzyEquals(right.AllowTracing)
               && left.Scope.FuzzyEquals(right.Scope);
    }
}