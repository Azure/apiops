using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ApiOperationPolicy
{
    public static async ValueTask ExportAll(ApiOperationDirectory apiOperationDirectory, ApiOperationUri apiOperationUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(apiOperationUri, listRestResources, cancellationToken)
                .ForEachParallel(async policyName => await Export(apiOperationDirectory, apiOperationUri, policyName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ApiOperationPolicyName> List(ApiOperationUri apiOperationUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var policiesUri = new ApiOperationPoliciesUri(apiOperationUri);
        var policyJsonObjects = listRestResources(policiesUri.Uri, cancellationToken);
        return policyJsonObjects.Select(json => json.GetStringProperty("name"))
                                .Select(name => new ApiOperationPolicyName(name));
    }

    private static async ValueTask Export(ApiOperationDirectory apiOperationDirectory, ApiOperationUri apiOperationUri, ApiOperationPolicyName policyName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var policyFile = new ApiOperationPolicyFile(policyName, apiOperationDirectory);

        var policiesUri = new ApiOperationPoliciesUri(apiOperationUri);
        var policyUri = new ApiOperationPolicyUri(policyName, policiesUri);
        var responseJson = await getRestResource(policyUri.Uri, cancellationToken);
        var policyContent = responseJson.GetJsonObjectProperty("properties")
                                        .GetStringProperty("value");

        logger.LogInformation("Writing API operation policy file {filePath}...", policyFile.Path);
        await policyFile.OverwriteWithText(policyContent, cancellationToken);
    }
}
