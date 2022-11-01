using common;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Api
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, OpenApiSpecification specification, IEnumerable<string>? apiNamesToExport, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                // Filter out apis that should not be exported
                .Where(apiName => ShouldExport(apiName, apiNamesToExport))
                // Export APIs in parallel
                .ForEachParallel(async apiName => await Export(serviceDirectory,
                                                               serviceUri,
                                                               apiName,
                                                               specification,
                                                               listRestResources,
                                                               getRestResource,
                                                               downloadResource,
                                                               cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ApiName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var apisUri = new ApisUri(serviceUri);
        var apiJsonObjects = listRestResources(apisUri.Uri, cancellationToken);
        return apiJsonObjects.Select(json => json.GetStringProperty("name"))
                             .Select(name => new ApiName(name));
    }

    private static bool ShouldExport(ApiName apiName, IEnumerable<string>? apiNamesToExport)
    {
        return apiNamesToExport is null
               || apiNamesToExport.Any(apiNameToExport => apiNameToExport.Equals(apiName.ToString(), StringComparison.OrdinalIgnoreCase)
                                                          // Apis with revisions have the format 'apiName;revision'. We split by semicolon to get the name.
                                                          || apiNameToExport.Equals(apiName.ToString()
                                                                                           .Split(';')
                                                                                           .First(),
                                                                                    StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ApiName apiName, OpenApiSpecification apiSpecification, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        var apisDirectory = new ApisDirectory(serviceDirectory);
        var apiDirectory = new ApiDirectory(apiName, apisDirectory);

        var apisUri = new ApisUri(serviceUri);
        var apiUri = new ApiUri(apiName, apisUri);

        await ExportInformationFile(apiDirectory, apiUri, apiName, getRestResource, cancellationToken);
        await ExportSpecificationFile(apiDirectory, apiUri, apiSpecification, getRestResource, downloadResource, cancellationToken);
        await ExportTags(apiDirectory, apiUri, listRestResources, cancellationToken);
        await ExportPolicies(apiDirectory, apiUri, listRestResources, getRestResource, cancellationToken);
        await ExportOperations(apiDirectory, apiUri, listRestResources, getRestResource, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(ApiDirectory apiDirectory, ApiUri apiUri, ApiName apiName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var apiInformationFile = new ApiInformationFile(apiDirectory);

        var responseJson = await getRestResource(apiUri.Uri, cancellationToken);
        var apiModel = ApiModel.Deserialize(apiName, responseJson);
        var contentJson = apiModel.Serialize();

        await apiInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }

    private static async ValueTask ExportSpecificationFile(ApiDirectory apiDirectory, ApiUri apiUri, OpenApiSpecification apiSpecification, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        var specificationFile = new ApiSpecificationFile(apiSpecification.Version, apiSpecification.Format, apiDirectory);

        var exportUri = new ApiSpecificationExportUri(apiUri).Uri;
        var exportResponse = await getRestResource(exportUri, cancellationToken);
        var downloadUri = new Uri(exportResponse.GetJsonObjectProperty("value")
                                                .GetStringProperty("link"));
        using var responseStream = await downloadResource(downloadUri, cancellationToken);
        using var specificationFileStream = await ConvertFileToOpenApiSpecification(responseStream, apiSpecification);

        await specificationFile.OverwriteWithStream(specificationFileStream, cancellationToken);
    }

    private static async ValueTask<MemoryStream> ConvertFileToOpenApiSpecification(Stream fileStream, OpenApiSpecification apiSpecification)
    {
        var readResult = await new OpenApiStreamReader().ReadAsync(fileStream);
        var memoryStream = new MemoryStream();
        readResult.OpenApiDocument.Serialize(memoryStream, apiSpecification.Version, apiSpecification.Format);
        memoryStream.Position = 0;

        return memoryStream;
    }

    private static async ValueTask ExportTags(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        await ApiTag.ExportAll(apiDirectory, apiUri, listRestResources, cancellationToken);
    }

    private static async ValueTask ExportPolicies(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await ApiPolicy.ExportAll(apiDirectory, apiUri, listRestResources, getRestResource, cancellationToken);
    }

    private static async ValueTask ExportOperations(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await ApiOperation.ExportAll(apiUri, apiDirectory, listRestResources, getRestResource, cancellationToken);
    }
}
