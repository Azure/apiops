using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ProductPolicyModel : ITestModel<ProductPolicyModel>
{
    public required string Content { get; init; }

    public required ResourceKey Key { get; init; }

    public JsonObject ToDto() =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["format"] = "rawxml",
                ["value"] = Content
            }
        };

    public Result<Unit> ValidateDto(JsonObject dto)
    {
        return validateContent();

        Result<Unit> validateContent() =>
            from properties in dto.GetJsonObjectProperty("properties")
            from value in properties.GetStringProperty("value")
            from unit in PolicyModule.FuzzyEquals(value, Content)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has policy content that doesn't match expected content.")
            select unit;
    }

    public static Gen<ImmutableHashSet<ProductPolicyModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from productModels in Generator.SubSetOf([.. models.OfType<ProductModel>()])
        from policyModels in Generator.Traverse(productModels,
                                                productModel => Generate(productModel, models))
        select policyModels.ToImmutableHashSet();

    private static Gen<string> GenerateContent(IEnumerable<ITestModel> models)
    {
        var namedValues = models.OfType<NamedValueModel>();
        var fragments = models.OfType<PolicyFragmentModel>();

        return from inboundSnippet in PolicyModule.GenerateInboundSnippet(namedValues, fragments)
               from outboundSnippet in PolicyModule.GenerateOutboundSnippet(namedValues, fragments)
               select $"""
                       <policies>
                           {inboundSnippet}
                           <backend>
                               <forward-request />
                           </backend>
                           {outboundSnippet}
                       </policies>
                       """;
    }

    private static Gen<ProductPolicyModel> Generate(ProductModel productModel, IEnumerable<ITestModel> models) =>
        from content in GenerateContent(models)
        select new ProductPolicyModel
        {
            Key = new ResourceKey
            {
                Resource = ProductPolicyResource.Instance,
                Name = PolicyModule.ResourceName,
                Parents = productModel.Key.AsParentChain()
            },
            Content = content
        };

    public static Gen<ImmutableHashSet<ProductPolicyModel>> GenerateUpdates(IEnumerable<ProductPolicyModel> productPolicyModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(productPolicyModels, model => GenerateUpdate(model, allModels))
        select updatedModels.ToImmutableHashSet();

    private static Gen<ProductPolicyModel> GenerateUpdate(ProductPolicyModel model, IEnumerable<ITestModel> allModels) =>
        from content in GenerateContent(allModels)
        select model with
        {
            Content = content
        };

    public static Gen<ImmutableHashSet<ProductPolicyModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}