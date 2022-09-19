using common;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Gateway
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async gatewayName => await Export(serviceDirectory,
                                                                   serviceUri,
                                                                   gatewayName,
                                                                   listRestResources,
                                                                   getRestResource,
                                                                   cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<GatewayName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var gatewaysUri = new GatewaysUri(serviceUri);
        var gatewayJsonObjects = listRestResources(gatewaysUri.Uri, cancellationToken);
        return gatewayJsonObjects.Select(json => json.GetStringProperty("name"))
                                 .Select(name => new GatewayName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, GatewayName gatewayName, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var gatewaysDirectory = new GatewaysDirectory(serviceDirectory);
        var gatewayDirectory = new GatewayDirectory(gatewayName, gatewaysDirectory);

        var gatewaysUri = new GatewaysUri(serviceUri);
        var gatewayUri = new GatewayUri(gatewayName, gatewaysUri);

        await ExportInformationFile(gatewayDirectory, gatewayUri, gatewayName, getRestResource, cancellationToken);
        await ExportApis(gatewayDirectory, gatewayUri, listRestResources, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(GatewayDirectory gatewayDirectory, GatewayUri gatewayUri, GatewayName gatewayName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var gatewayInformationFile = new GatewayInformationFile(gatewayDirectory);

        var responseJson = await getRestResource(gatewayUri.Uri, cancellationToken);
        var gatewayModel = GatewayModel.Deserialize(gatewayName, responseJson);
        var contentJson = gatewayModel.Serialize();

        await gatewayInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }

    private static async ValueTask ExportApis(GatewayDirectory gatewayDirectory, GatewayUri gatewayUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        await GatewayApi.ExportAll(gatewayDirectory, gatewayUri, listRestResources, cancellationToken);
    }
}