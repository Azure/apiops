using common;
using CsCheck;

namespace integration.tests;

internal sealed record ApiPolicyModel : IPolicyResourceTestModel<ApiPolicyModel>
{
    public required string Content { get; init; }

    public static IPolicyResource AssociatedResource { get; } = ApiPolicyResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var option = from predecessorsGen in Generator.GeneratePredecessors<ApiPolicyModel>(baseline)
                     let newGenerator = from predecessors in predecessorsGen
                                        from model in Generate()
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));
    }

    private static Gen<ApiPolicyModel> Generate() =>
        from content in GenerateContent()
        select new ApiPolicyModel
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

    private static Gen<ApiPolicyModel> GenerateUpdate(ApiPolicyModel model) =>
        from content in GenerateContent()
        select model with { Content = content };
}