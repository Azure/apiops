using common;
using CsCheck;

namespace integration.tests;

internal sealed record GatewayApiModel : ICompositeResourceTestModel<GatewayApiModel>
{
    public ResourceName Name => SecondaryResourceName;
    public required ResourceName PrimaryResourceName { get; init; }
    public required ResourceName SecondaryResourceName { get; init; }

    public static ICompositeResource AssociatedResource { get; } = GatewayApiResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var option = from predecessorsGen in Generator.GeneratePredecessors<GatewayApiModel>(baseline)
                     let newGenerator = from predecessors in predecessorsGen
                                        let gatewayName = predecessors.PickNameOrThrow<GatewayResource>()
                                        let apiName = predecessors.PickNameOrThrow<ApiResource>()
                                        let model = new GatewayApiModel
                                        {
                                            PrimaryResourceName = gatewayName,
                                            SecondaryResourceName = apiName
                                        }
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, Gen.Const, newGenerator);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));
    }
}