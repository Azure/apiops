using common;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ServicePolicy
{
    public static async ValueTask ExportAll(ServiceUri serviceUri, ServiceDirectory serviceDirectory, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async policyName => await Export(serviceDirectory,
                                                                  serviceUri,
                                                                  policyName,
                                                                  getRestResource,
                                                                  cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ServicePolicyName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var policiesUri = new ServicePoliciesUri(serviceUri);
        var policyJsonObjects = listRestResources(policiesUri.Uri, cancellationToken);
        return policyJsonObjects.Select(json => json.GetStringProperty("name"))
                                .Select(name => new ServicePolicyName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ServicePolicyName policyName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var policyFile = new ServicePolicyFile(policyName, serviceDirectory);

        var policiesUri = new ServicePoliciesUri(serviceUri);
        var policyUri = new ServicePolicyUri(policyName, policiesUri);
        var responseJson = await getRestResource(policyUri.Uri, cancellationToken);
        var policyContent = responseJson.GetJsonObjectProperty("properties")
                                        .GetStringProperty("value");

        await policyFile.OverwriteWithText(policyContent, cancellationToken);
    }
}
