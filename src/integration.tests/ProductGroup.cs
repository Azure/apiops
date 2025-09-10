using common;
using CsCheck;
using System.Collections.Generic;

namespace integration.tests;

internal sealed record ProductGroupModel : ILinkResourceTestModel<ProductGroupModel>
{
    public required ResourceName PrimaryResourceName { get; init; }
    public required ResourceName SecondaryResourceName { get; init; }

    public static ILinkResource AssociatedResource { get; } = ProductGroupResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var option = from predecessorsGen in Generator.GeneratePredecessors<ProductGroupModel>(baseline)
                     let newGenerator = from predecessors in predecessorsGen
                                        let productName = predecessors.PickNameOrThrow<ProductResource>()
                                        let groupName = predecessors.PickNameOrThrow<GroupResource>()
                                        let model = new ProductGroupModel
                                        {
                                            PrimaryResourceName = productName,
                                            SecondaryResourceName = groupName
                                        }
                                        select (model, predecessors)
                     select Generator.GenerateNodes<ProductGroupModel>(baseline, Gen.Const, newGenerator)
                                     .Where(setIsValid);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));

        // Products can only be linked to a single group
        bool setIsValid(ModelNodeSet set)
        {
            var names = new HashSet<ResourceName>();

            foreach (var node in set)
            {
                if (node.Model is ProductGroupModel model
                    && names.Add(model.PrimaryResourceName) is false)
                {
                    return false;
                }
            }

            return true;
        }
    }
}