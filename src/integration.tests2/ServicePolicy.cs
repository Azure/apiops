using common;
using common.tests;
using CsCheck;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ServicePolicyModel : ITestModel<ServicePolicyModel>
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

    private static Gen<ServicePolicyModel> Generate(IEnumerable<ITestModel> models) =>
        from content in GenerateContent(models)
        select new ServicePolicyModel
        {
            Key = ResourceKey.From(ServicePolicyResource.Instance, PolicyModule.ResourceName),
            Content = content
        };

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

    private static Gen<ImmutableHashSet<ServicePolicyModel>> EmptySetGenerator { get; } =
        Gen.Const(ImmutableHashSet<ServicePolicyModel>.Empty);

    private static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateSingletonSet(IEnumerable<ITestModel> models) =>
        from model in Generate(models)
        select ImmutableHashSet.Create(model);

    public static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Gen.Frequency((1, EmptySetGenerator), (5, GenerateSingletonSet(models)));

    public static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateUpdates(IEnumerable<ServicePolicyModel> models) =>
        from updatedModels in Generator.Traverse(models, model => GenerateUpdate(model, models))
        select updatedModels.ToImmutableHashSet();

    private static Gen<ServicePolicyModel> GenerateUpdate(ServicePolicyModel model, IEnumerable<ITestModel> allModels) =>
        from content in GenerateContent(allModels)
        select model with
        {
            Content = content
        };

    public static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        Gen.Frequency((1, EmptySetGenerator), (5, GenerateSingletonSet(accumulatedNextModels)));
}
