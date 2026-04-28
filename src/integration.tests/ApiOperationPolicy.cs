using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ApiOperationPolicyModel : ITestModel<ApiOperationPolicyModel>
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

    public static Gen<ImmutableHashSet<ApiOperationPolicyModel>> GenerateSet(IEnumerable<ITestModel> models)
    {
        var keys = models.OfType<ApiModel>()
                         .SelectMany(model =>
                         {
                             var operationNames = model.OperationNames.IfNone(() => []);
                             operationNames = model.Type switch
                             {// APIM randomly generates operation names for SOAP and WADL APIs, so we cannot reliably generate policies for them.
                                 ApiType.Wsdl => [],
                                 ApiType.Wadl => [],
                                 _ => operationNames
                             };

                             return from operationName in operationNames
                                    select new ResourceKey
                                    {
                                        Resource = ApiOperationPolicyResource.Instance,
                                        Name = PolicyModule.ResourceName,
                                        Parents = model.Key
                                                       .AsParentChain()
                                                       .Append(ApiOperationResource.Instance, operationName)
                                    };
                         });

        return from selectedKeys in Generator.SubSetOf([.. keys])
               from policyModels in Generator.Traverse(selectedKeys,
                                                       key => from content in GenerateContent(models)
                                                              select new ApiOperationPolicyModel
                                                              {
                                                                  Key = key,
                                                                  Content = content
                                                              })
               select policyModels.ToImmutableHashSet();
    }

    private static Gen<string> GenerateContent(IEnumerable<ITestModel> models)
    {
        var namedValues = models.OfType<NamedValueModel>();
        var fragments = models.OfType<PolicyFragmentModel>();

        return from inboundSnippet in PolicyModule.GenerateInboundSnippet(namedValues, fragments)
               from outboundSnippet in PolicyModule.GenerateOutboundSnippet(namedValues, fragments)
               select $"""
                       <policies>
                           {inboundSnippet}
                           {outboundSnippet}
                       </policies>
                       """;
    }

    public static Gen<ImmutableHashSet<ApiOperationPolicyModel>> GenerateUpdates(IEnumerable<ApiOperationPolicyModel> apiOperationPolicyModels, IEnumerable<ITestModel> allModels) =>
        from updatedModels in Generator.Traverse(apiOperationPolicyModels, model => GenerateUpdate(model, allModels))
        select updatedModels.ToImmutableHashSet();

    private static Gen<ApiOperationPolicyModel> GenerateUpdate(ApiOperationPolicyModel model, IEnumerable<ITestModel> allModels) =>
        from content in GenerateContent(allModels)
        select model with
        {
            Content = content
        };

    public static Gen<ImmutableHashSet<ApiOperationPolicyModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        GenerateSet(accumulatedNextModels);
}