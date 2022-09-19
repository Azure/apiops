using common;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Backend
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async backendName => await Export(serviceDirectory,
                                                                   serviceUri,
                                                                   backendName,
                                                                   getRestResource,
                                                                   cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<BackendName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var backendsUri = new BackendsUri(serviceUri);
        var backendJsonObjects = listRestResources(backendsUri.Uri, cancellationToken);
        return backendJsonObjects.Select(json => json.GetStringProperty("name"))
                                 .Select(name => new BackendName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, BackendName backendName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var backendsDirectory = new BackendsDirectory(serviceDirectory);
        var backendDirectory = new BackendDirectory(backendName, backendsDirectory);

        var backendsUri = new BackendsUri(serviceUri);
        var backendUri = new BackendUri(backendName, backendsUri);

        await ExportInformationFile(backendDirectory, backendUri, backendName, getRestResource, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(BackendDirectory backendDirectory, BackendUri backendUri, BackendName backendName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var backendInformationFile = new BackendInformationFile(backendDirectory);

        var responseJson = await getRestResource(backendUri.Uri, cancellationToken);
        var backendModel = BackendModel.Deserialize(backendName, responseJson);
        var contentJson = backendModel.Serialize();

        await backendInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}