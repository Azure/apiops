using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ApiVersionSet
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async apiVersionSetName => await Export(serviceDirectory, serviceUri, apiVersionSetName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ApiVersionSetName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var apiVersionSetsUri = new ApiVersionSetsUri(serviceUri);
        var apiVersionSetJsonObjects = listRestResources(apiVersionSetsUri.Uri, cancellationToken);
        return apiVersionSetJsonObjects.Select(json => json.GetStringProperty("name"))
                                 .Select(name => new ApiVersionSetName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ApiVersionSetName apiVersionSetName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiVersionSetsDirectory = new ApiVersionSetsDirectory(serviceDirectory);
        var apiVersionSetDirectory = new ApiVersionSetDirectory(apiVersionSetName, apiVersionSetsDirectory);

        var apiVersionSetsUri = new ApiVersionSetsUri(serviceUri);
        var apiVersionSetUri = new ApiVersionSetUri(apiVersionSetName, apiVersionSetsUri);

        await ExportInformationFile(apiVersionSetDirectory, apiVersionSetUri, apiVersionSetName, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(ApiVersionSetDirectory apiVersionSetDirectory, ApiVersionSetUri apiVersionSetUri, ApiVersionSetName apiVersionSetName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiVersionSetInformationFile = new ApiVersionSetInformationFile(apiVersionSetDirectory);

        var responseJson = await getRestResource(apiVersionSetUri.Uri, cancellationToken);
        var apiVersionSetModel = ApiVersionSetModel.Deserialize(apiVersionSetName, responseJson);
        var contentJson = apiVersionSetModel.Serialize();

        logger.LogInformation("Writing API version set information file {filePath}...", apiVersionSetInformationFile.Path);
        await apiVersionSetInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}