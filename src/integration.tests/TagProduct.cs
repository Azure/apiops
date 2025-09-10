using common;
using CsCheck;
using System.Collections.Generic;
using System.Linq;

namespace integration.tests;

internal sealed record TagProductModel : ILinkResourceTestModel<TagProductModel>
{
    public required ResourceName PrimaryResourceName { get; init; }
    public required ResourceName SecondaryResourceName { get; init; }

    public static ILinkResource AssociatedResource { get; } = TagProductResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var option = from predecessorsGen in Generator.GeneratePredecessors<TagProductModel>(baseline)
                     let newGenerator = from predecessors in predecessorsGen
                                        let tagName = predecessors.PickNameOrThrow<TagResource>()
                                        let productName = predecessors.PickNameOrThrow<ProductResource>()
                                        let model = new TagProductModel
                                        {
                                            PrimaryResourceName = tagName,
                                            SecondaryResourceName = productName
                                        }
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, Gen.Const, newGenerator)
                                     .Where(setIsValid);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));

        // PRODUCTs can only be linked to a single tag
        bool setIsValid(ModelNodeSet set)
        {
            var names = new HashSet<ResourceName>();

            foreach (var node in set)
            {
                if (node.Model is TagProductModel model
                    && names.Add(model.SecondaryResourceName) is false)
                {
                    return false;
                }
            }

            return true;
        }
    }
}