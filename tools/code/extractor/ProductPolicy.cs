using common;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ProductPolicy
{
    public static async ValueTask ExportAll(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await List(productUri, listRestResources, cancellationToken)
                .ForEachParallel(async policyName => await Export(productDirectory,
                                                                  productUri,
                                                                  policyName,
                                                                  getRestResource,
                                                                  cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ProductPolicyName> List(ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var policiesUri = new ProductPoliciesUri(productUri);
        var policyJsonObjects = listRestResources(policiesUri.Uri, cancellationToken);
        return policyJsonObjects.Select(json => json.GetStringProperty("name"))
                                .Select(name => new ProductPolicyName(name));
    }

    private static async ValueTask Export(ProductDirectory productDirectory, ProductUri productUri, ProductPolicyName policyName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var policyFile = new ProductPolicyFile(policyName, productDirectory);

        var policiesUri = new ProductPoliciesUri(productUri);
        var policyUri = new ProductPolicyUri(policyName, policiesUri);
        var responseJson = await getRestResource(policyUri.Uri, cancellationToken);
        var policyContent = responseJson.GetJsonObjectProperty("properties")
                                        .GetStringProperty("value");

        await policyFile.OverwriteWithText(policyContent, cancellationToken);
    }
}
