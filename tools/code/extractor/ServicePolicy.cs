using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ServicePolicy
{
    public static async ValueTask ExportAll(ServiceUri serviceUri, ServiceDirectory serviceDirectory, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async policyName => await Export(serviceDirectory, serviceUri, policyName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ServicePolicyName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var policiesUri = new ServicePoliciesUri(serviceUri);
        var policyJsonObjects = listRestResources(policiesUri.Uri, cancellationToken);
        return policyJsonObjects.Select(json => json.GetStringProperty("name"))
                                .Select(name => new ServicePolicyName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ServicePolicyName policyName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var policyFile = new ServicePolicyFile(policyName, serviceDirectory);

        var policiesUri = new ServicePoliciesUri(serviceUri);
        var policyUri = new ServicePolicyUri(policyName, policiesUri);
        var responseJson = await getRestResource(policyUri.Uri, cancellationToken);
        var policyContent = responseJson.GetJsonObjectProperty("properties")
                                        .GetStringProperty("value");

        logger.LogInformation("Writing service policy file {filePath}...", policyFile.Path);
        await policyFile.OverwriteWithText(policyContent, cancellationToken);
    }
}
