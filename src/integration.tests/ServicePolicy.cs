using common;
using CsCheck;

namespace integration.tests;

internal sealed record ServicePolicyModel : IPolicyResourceTestModel<ServicePolicyModel>
{
    public required string Content { get; init; }

    public static IPolicyResource AssociatedResource { get; } = ServicePolicyResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var newGenerator = from model in Generate()
                           select (model, ModelNodeSet.Empty);

        return Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);
    }

    private static Gen<ServicePolicyModel> Generate() =>
        from content in GenerateContent()
        select new ServicePolicyModel
        {
            Content = content
        };

    private static Gen<string> GenerateContent() =>
        from inboundSnippet in Generator.InboundPolicySnippet
        from outboundSnippet in Generator.OutboundPolicySnippet
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

    private static Gen<ServicePolicyModel> GenerateUpdate(ServicePolicyModel model) =>
        from content in GenerateContent()
        select model with { Content = content };
}