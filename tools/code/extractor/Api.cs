using common;
using Flurl;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static common.ApiModel.ApiCreateOrUpdateProperties;

namespace extractor;

internal static class Api
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, DefaultApiSpecification defaultSpecification, IEnumerable<string>? apiNamesToExport, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                // Filter out apis that should not be exported
                .Where(apiName => ShouldExport(apiName, apiNamesToExport))
                // Export APIs in parallel
                .ForEachParallel(async apiName => await Export(serviceDirectory,
                                                               serviceUri,
                                                               apiName,
                                                               defaultSpecification,
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

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ApiName apiName, DefaultApiSpecification defaultSpecification, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        var apisDirectory = new ApisDirectory(serviceDirectory);
        var apiDirectory = new ApiDirectory(apiName, apisDirectory);

        var apisUri = new ApisUri(serviceUri);
        var apiUri = new ApiUri(apiName, apisUri);

        var apiResponseJson = await getRestResource(apiUri.Uri, cancellationToken);
        var apiModel = ApiModel.Deserialize(apiName, apiResponseJson);

        await ExportInformationFile(apiModel, apiDirectory, cancellationToken);
        await ExportSpecification(apiModel, apiDirectory, apiUri, defaultSpecification, getRestResource, downloadResource, cancellationToken);
        await ExportTags(apiDirectory, apiUri, listRestResources, cancellationToken);
        await ExportPolicies(apiDirectory, apiUri, listRestResources, getRestResource, cancellationToken);
        await ExportOperations(apiDirectory, apiUri, listRestResources, getRestResource, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(ApiModel apiModel, ApiDirectory apiDirectory, CancellationToken cancellationToken)
    {
        var apiInformationFile = new ApiInformationFile(apiDirectory);
        var contentJson = apiModel.Serialize();

        await apiInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }

    private static async ValueTask ExportSpecification(ApiModel apiModel, ApiDirectory apiDirectory, ApiUri apiUri, DefaultApiSpecification defaultSpecification, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        await (apiModel.Properties.Type switch
        {
            var apiType when apiType == ApiTypeOption.GraphQl => ExportGraphQlSpecification(apiDirectory, apiUri, getRestResource, cancellationToken),
            var apiType when apiType == ApiTypeOption.Soap => ExportWsdlSpecification(apiDirectory, apiUri, getRestResource, downloadResource, cancellationToken),
            var apiType when apiType == ApiTypeOption.WebSocket => ValueTask.CompletedTask,
            _ => ExportApiSpecification(apiDirectory, apiUri, defaultSpecification, getRestResource, downloadResource, cancellationToken)
        });
    }

    private static async ValueTask ExportGraphQlSpecification(ApiDirectory apiDirectory, ApiUri apiUri, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var schemaName = ApiSchemaName.GraphQl;
        var schemasUri = new ApiSchemasUri(apiUri);
        var schemaUri = new ApiSchemaUri(schemaName, schemasUri);
        var schemaJson = await getRestResource(schemaUri.Uri, cancellationToken);
        var schemaModel = ApiSchemaModel.Deserialize(schemaName, schemaJson);
        var schemaText = schemaModel.Properties.Document?.Value;

        if (schemaText is not null)
        {
            var specificationFile = new ApiSpecificationFile.GraphQl(apiDirectory);
            await specificationFile.OverwriteWithText(schemaText, cancellationToken);
        }
    }

    private static async ValueTask ExportWsdlSpecification(ApiDirectory apiDirectory, ApiUri apiUri, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        var specificationFile = new ApiSpecificationFile.Wsdl(apiDirectory);
        using var fileStream = await DownloadSpecificationFile(apiUri, format: "wsdl-link", getRestResource, downloadResource, cancellationToken);
        await specificationFile.OverwriteWithStream(fileStream, cancellationToken);
    }

    private static async ValueTask<Stream> DownloadSpecificationFile(ApiUri apiUri, string format, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        var exportUri = apiUri.Uri.SetQueryParam("format", format)
                                  .SetQueryParam("export", "true")
                                  .SetQueryParam("api-version", "2021-08-01")
                                  .ToUri();

        var exportResponse = await getRestResource(exportUri, cancellationToken);
        var downloadUri = new Uri(exportResponse.GetJsonObjectProperty("value")
                                                .GetStringProperty("link"));

        return await downloadResource(downloadUri, cancellationToken);
    }

    private static async ValueTask ExportApiSpecification(ApiDirectory apiDirectory, ApiUri apiUri, DefaultApiSpecification defaultSpecification, GetRestResource getRestResource, DownloadResource downloadResource, CancellationToken cancellationToken)
    {
        ApiSpecificationFile specificationFile = defaultSpecification switch
        {
            DefaultApiSpecification.Wadl => new ApiSpecificationFile.Wadl(apiDirectory),
            DefaultApiSpecification.OpenApi openApi => new ApiSpecificationFile.OpenApi(openApi.Version, openApi.Format, apiDirectory),
            _ => throw new NotSupportedException()
        };

        var format = defaultSpecification switch
        {
            DefaultApiSpecification.Wadl => "wadl-link",
            DefaultApiSpecification.OpenApi => "openapi-link",
            _ => throw new NotSupportedException()
        };

        using var fileStream = await DownloadSpecificationFile(apiUri, format, getRestResource, downloadResource, cancellationToken);
        using var specificationFileStream = defaultSpecification switch
        {
            DefaultApiSpecification.OpenApi openApi => await ConvertFileToOpenApiSpecification(fileStream, openApi),
            _ => fileStream
        };

        await specificationFile.OverwriteWithStream(specificationFileStream, cancellationToken);
    }

    private static async ValueTask<MemoryStream> ConvertFileToOpenApiSpecification(Stream fileStream, DefaultApiSpecification.OpenApi openApiSpecification)
    {
        var readResult = await new OpenApiStreamReader().ReadAsync(fileStream);
        var memoryStream = new MemoryStream();
        readResult.OpenApiDocument.Serialize(memoryStream, openApiSpecification.Version, openApiSpecification.Format);
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
