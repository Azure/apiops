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
            from unit in CommonModule.FuzzyEqualsPolicy(value, Content)
                        ? Result.Success(Unit.Instance)
                        : Error.From($"Resource '{Key}' has policy content that doesn't match expected content.")
            select unit;
    }

    private static Gen<ServicePolicyModel> Generator { get; } =
        from content in GenerateContent()
        select new ServicePolicyModel
        {
            Key = ResourceKey.From(ServicePolicyResource.Instance, CommonModule.PolicyName),
            Content = content
        };

    private static Gen<string> GenerateContent() =>
        from inboundSnippet in CommonModule.InboundPolicySnippet
        from outboundSnippet in CommonModule.OutboundPolicySnippet
        select $"""
                <policies>
                    {inboundSnippet}
                    <backend>
                        <forward-request />
                    </backend>
                    {outboundSnippet}
                    <on-error />
                </policies>
                """;

    private static Gen<ImmutableHashSet<ServicePolicyModel>> EmptySetGen { get; } =
        Gen.Const(ImmutableHashSet<ServicePolicyModel>.Empty);

    private static Gen<ImmutableHashSet<ServicePolicyModel>> SingletonSetGen { get; } =
        from model in Generator
        select ImmutableHashSet.Create(model);

    public static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateSet(IEnumerable<ITestModel> models) =>
        Gen.Frequency((1, EmptySetGen), (5, SingletonSetGen));

    public static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateUpdates(IEnumerable<ServicePolicyModel> models) =>
        from updatedModels in
            common.tests.Generator
                        .Traverse(models, GenerateUpdate)
        select models.Any()
            ? ImmutableHashSet.Create(updatedModels.Single())
            : ImmutableHashSet<ServicePolicyModel>.Empty;

    private static Gen<ServicePolicyModel> GenerateUpdate(ServicePolicyModel model) =>
        from content in GenerateContent()
        select model with
        {
            Content = content
        };

    public static Gen<ImmutableHashSet<ServicePolicyModel>> GenerateNextState(IEnumerable<ITestModel> previousModels, IEnumerable<ITestModel> accumulatedNextModels) =>
        Gen.Frequency((1, EmptySetGen), (5, SingletonSetGen));
}
