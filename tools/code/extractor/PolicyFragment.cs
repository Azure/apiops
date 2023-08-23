using common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class PolicyFragment
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, IEnumerable<string>? policyFragmentNamesToExport, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .Where(policyFragmentName => ShouldExport(policyFragmentName, policyFragmentNamesToExport))
                .ForEachParallel(async policyFragmentName => await Export(serviceDirectory, serviceUri, policyFragmentName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<PolicyFragmentName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var policyFragmentsUri = new PolicyFragmentsUri(serviceUri);
        var policyFragmentJsonObjects = listRestResources(policyFragmentsUri.Uri, cancellationToken);
        return policyFragmentJsonObjects.Select(json => json.GetStringProperty("name"))
                                        .Select(name => new PolicyFragmentName(name));
    }
    private static bool ShouldExport(PolicyFragmentName policyFragmentName, IEnumerable<string>? policyFragmentNamesToExport)
    {
        return policyFragmentNamesToExport is null
               || policyFragmentNamesToExport.Any(policyFragmentNameToExport => policyFragmentNameToExport.Equals(policyFragmentName.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, PolicyFragmentName policyFragmentName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var policyFragmentsDirectory = new PolicyFragmentsDirectory(serviceDirectory);
        var policyFragmentDirectory = new PolicyFragmentDirectory(policyFragmentName, policyFragmentsDirectory);

        var policyFragmentsUri = new PolicyFragmentsUri(serviceUri);
        var policyFragmentUri = new PolicyFragmentUri(policyFragmentName, policyFragmentsUri);
        var policyFragmentJson = await getRestResource(policyFragmentUri.Uri, cancellationToken);

        await ExportInformationFile(policyFragmentDirectory, policyFragmentName, policyFragmentJson, logger, cancellationToken);
        await ExportPolicyFile(policyFragmentDirectory, policyFragmentJson, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(PolicyFragmentDirectory policyFragmentDirectory, PolicyFragmentName policyFragmentName, JsonObject policyFragmentJson, ILogger logger, CancellationToken cancellationToken)
    {
        var policyFragmentInformationFile = new PolicyFragmentInformationFile(policyFragmentDirectory);
        var policyFragmentModel = PolicyFragmentModel.Deserialize(policyFragmentName, policyFragmentJson);
        var contentJson = policyFragmentModel.Serialize();

        logger.LogInformation("Writing policy fragment information file {filePath}...", policyFragmentInformationFile.Path);
        await policyFragmentInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }

    private static async ValueTask ExportPolicyFile(PolicyFragmentDirectory policyFragmentDirectory, JsonObject policyFragmentJson, ILogger logger, CancellationToken cancellationToken)
    {
        var policyFile = new PolicyFragmentPolicyFile(policyFragmentDirectory);

        var policyText = policyFragmentJson.GetJsonObjectProperty("properties")
                                           .GetStringProperty("value");

        logger.LogInformation("Writing policy fragment policy file {filePath}...", policyFile.Path);
        await policyFile.OverwriteWithText(policyText, cancellationToken);
    }
}
