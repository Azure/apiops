using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ApiPolicyModel : ITestModel<ApiPolicyModel>
{
    public required ResourceKey Key { get; init; }

    public required string Content { get; init; }

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
        return from properties in dto.GetJsonObjectProperty("properties")
               from value in properties.GetStringProperty("value")
               from unit in PolicyModule.FuzzyEquals(value, Content)
                           ? Result.Success(Unit.Instance)
                           : Error.From($"Resource '{Key}' has policy content that doesn't match expected content.")
               select unit;
    }

    public static Gen<ImmutableHashSet<ApiPolicyModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        from apiModels in Generator.SubSetOf([.. models.OfType<ApiModel>()])
        from policyModels in Generator.Traverse(apiModels,
                                                apiModel => Generate(apiModel, models))
        select policyModels.ToImmutableHashSet();

    private static Gen<ApiPolicyModel> Generate(ApiModel apiModel, IEnumerable<ITestModel> models) =>
        from content in GenerateContent(models)
        select new ApiPolicyModel
        {
            Key = new ResourceKey
            {
                Resource = ApiPolicyResource.Instance,
                Name = PolicyModule.ResourceName,
                Parents = apiModel.Key.AsParentChain()
            },
            Content = content
        };

    private static Gen<string> GenerateContent(IEnumerable<ITestModel> models)
    {
        var namedValues = models.OfType<NamedValueModel>();
        var fragments = models.OfType<PolicyFragmentModel>();
        var backends = models.OfType<BackendModel>();

        return from inboundSnippet in PolicyModule.GenerateInboundSnippet(namedValues, fragments, backends)
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

    public static Gen<ImmutableHashSet<ApiPolicyModel>> GenerateUpdates(IEnumerable<ApiPolicyModel> apiPolicyModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(apiPolicyModels,
                                                 model => GenerateUpdate(model, allModels))
        select updatedModels.ToImmutableHashSet();

    private static Gen<ApiPolicyModel> GenerateUpdate(ApiPolicyModel model, IEnumerable<ITestModel> allModels) =>
        from content in GenerateContent(allModels)
        select model with
        {
            Content = content
        };

    public static Gen<ImmutableHashSet<ApiPolicyModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}
