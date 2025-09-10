using common;
using CsCheck;
using System.Collections.Generic;
using System.Linq;

namespace integration.tests;

internal sealed record TagApiModel : ILinkResourceTestModel<TagApiModel>
{
    public required ResourceName PrimaryResourceName { get; init; }
    public required ResourceName SecondaryResourceName { get; init; }

    public static ILinkResource AssociatedResource { get; } = TagApiResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var option = from predecessorsGen in Generator.GeneratePredecessors<TagApiModel>(baseline)
                     let newGenerator = from predecessors in predecessorsGen
                                        let tagName = predecessors.PickNameOrThrow<TagResource>()
                                        let apiName = predecessors.PickNameOrThrow<ApiResource>()
                                        let model = new TagApiModel
                                        {
                                            PrimaryResourceName = tagName,
                                            SecondaryResourceName = apiName
                                        }
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, Gen.Const, newGenerator)
                                     .Where(setIsValid);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));

        // APIs can only be linked to a single tag
        bool setIsValid(ModelNodeSet set)
        {
            var names = new HashSet<ResourceName>();

            foreach (var node in set)
            {
                if (node.Model is TagApiModel model
                    && names.Add(model.SecondaryResourceName) is false)
                {
                    return false;
                }
            }

            return true;
        }
    }
}