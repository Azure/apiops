using common;
using Flurl;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using SharpYaml.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using static common.ApiModel.ApiCreateOrUpdateProperties;

namespace extractor;

internal static class Api
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, DefaultApiSpecification defaultSpecification, IEnumerable<string>? apiNamesToExport, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                // Filter out apis that should not be exported
                .Where(apiName => ShouldExport(apiName, apiNamesToExport))
                // Export APIs in parallel
                .ForEachParallel(async apiName => await Export(serviceDirectory, serviceUri, apiName, defaultSpecification, listRestResources, getRestResource, downloadResource, logger, cancellationToken),
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

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ApiName apiName, DefaultApiSpecification defaultSpecification, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apisDirectory = new ApisDirectory(serviceDirectory);
        var apiDirectory = new ApiDirectory(apiName, apisDirectory);

        var apisUri = new ApisUri(serviceUri);
        var apiUri = new ApiUri(apiName, apisUri);

        var apiResponseJson = await getRestResource(apiUri.Uri, cancellationToken);
        var apiModel = ApiModel.Deserialize(apiName, apiResponseJson);

        await ExportInformationFile(apiModel, apiDirectory, logger, cancellationToken);
        await ExportSpecification(apiModel, apiDirectory, apiUri, defaultSpecification, getRestResource, downloadResource, logger, cancellationToken);
        await ExportTags(apiDirectory, apiUri, listRestResources, logger, cancellationToken);
        await ExportPolicies(apiDirectory, apiUri, listRestResources, getRestResource, logger, cancellationToken);
        await ExportDiagnostics(apiDirectory, apiUri, listRestResources, getRestResource, logger, cancellationToken);
        await ExportOperations(apiDirectory, apiUri, listRestResources, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(ApiModel apiModel, ApiDirectory apiDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        var apiInformationFile = new ApiInformationFile(apiDirectory);
        var contentJson = apiModel.Serialize();

        logger.LogInformation("Writing API information file {filePath}...", apiInformationFile.Path);
        await apiInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }

    private static async ValueTask ExportSpecification(ApiModel apiModel, ApiDirectory apiDirectory, ApiUri apiUri, DefaultApiSpecification defaultSpecification, GetRestResource getRestResource, DownloadResource downloadResource, ILogger logger, CancellationToken cancellationToken)
    {
        await (apiModel.Properties.Type switch
        {
            var apiType when apiType == ApiTypeOption.GraphQl => ExportGraphQlSpecification(apiDirectory, apiUri, getRestResource, logger, cancellationToken),
            var apiType when apiType == ApiTypeOption.Soap => ExportWsdlSpecification(apiDirectory, apiUri, getRestResource, downloadResource, logger, cancellationToken),
            var apiType when apiType == ApiTypeOption.WebSocket => ValueTask.CompletedTask,
            _ => ExportApiSpecification(apiDirectory, apiUri, defaultSpecification, getRestResource, downloadResource, logger, cancellationToken)
        });
    }

    private static async ValueTask ExportGraphQlSpecification(ApiDirectory apiDirectory, ApiUri apiUri, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
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

            logger.LogInformation("Writing API specification file {filePath}...", specificationFile.Path);
            await specificationFile.OverwriteWithText(schemaText, cancellationToken);
        }
    }

    private static async ValueTask ExportWsdlSpecification(ApiDirectory apiDirectory, ApiUri apiUri, GetRestResource getRestResource, DownloadResource downloadResource, ILogger logger, CancellationToken cancellationToken)
    {
        var specificationFile = new ApiSpecificationFile.Wsdl(apiDirectory);
        using var fileStream = await DownloadSpecificationFile(apiUri, format: "wsdl-link", getRestResource, downloadResource, cancellationToken);

        logger.LogInformation("Writing API specification file {filePath}...", specificationFile.Path);
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

    private static async ValueTask ExportApiSpecification(ApiDirectory apiDirectory, ApiUri apiUri, DefaultApiSpecification defaultSpecification, GetRestResource getRestResource, DownloadResource downloadResource, ILogger logger, CancellationToken cancellationToken)
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
            DefaultApiSpecification.OpenApi openApi =>
                openApi.Version switch
                {
                    OpenApiSpecVersion.OpenApi2_0 => "swagger-link",
                    OpenApiSpecVersion.OpenApi3_0 => "openapi-link",
                    _ => throw new NotSupportedException()
                },
            _ => throw new NotSupportedException()
        };

        using var downloadFileStream = await DownloadSpecificationFile(apiUri, format, getRestResource, downloadResource, cancellationToken);

        logger.LogInformation("Writing API specification file {filePath}...", specificationFile.Path);
        switch (defaultSpecification)
        {
            // APIM exports OpenApiv3 to YAML. Convert to JSON if needed.
            case DefaultApiSpecification.OpenApi openApi when openApi.Version is OpenApiSpecVersion.OpenApi3_0 && openApi.Format is OpenApiFormat.Json:
                {
                    var bytes = ConvertYamlStreamToJson(downloadFileStream);
                    await specificationFile.OverwriteWithBytes(bytes, cancellationToken);
                    break;
                }
            // APIM exports OpenApiv2 to JSON. Convert to YAML if needed.
            case DefaultApiSpecification.OpenApi openApi when openApi.Version is OpenApiSpecVersion.OpenApi2_0 && openApi.Format is OpenApiFormat.Yaml:
                {
                    var bytes = ConvertJsonStreamToYaml(downloadFileStream);
                    await specificationFile.OverwriteWithBytes(bytes, cancellationToken);
                    break;
                }
            default:
                await specificationFile.OverwriteWithStream(downloadFileStream, cancellationToken);
                break;
        }
    }

    private static byte[] ConvertYamlStreamToJson(Stream stream)
    {
        var yaml = ConvertStreamToYamlObject(stream);
        return JsonSerializer.SerializeToUtf8Bytes(yaml, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object ConvertStreamToYamlObject(Stream stream)
    {
        using var streamReader = new StreamReader(stream);
        return new Deserializer().Deserialize(streamReader) ?? throw new InvalidOperationException("Failed to deserialize YAML.");
    }

    private static byte[] ConvertJsonStreamToYaml(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        using var streamWriter = new StreamWriter(memoryStream);
        var yaml = ConvertStreamToYamlObject(stream);

        new Serializer().Serialize(streamWriter, yaml);
        memoryStream.Position = 0;

        return memoryStream.ToArray();
    }

    private static async ValueTask ExportTags(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, ILogger logger, CancellationToken cancellationToken)
    {
        await ApiTag.ExportAll(apiDirectory, apiUri, listRestResources, logger, cancellationToken);
    }

    private static async ValueTask ExportDiagnostics(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await ApiDiagnostic.ExportAll(apiUri, apiDirectory, listRestResources, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportPolicies(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await ApiPolicy.ExportAll(apiDirectory, apiUri, listRestResources, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportOperations(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await ApiOperation.ExportAll(apiUri, apiDirectory, listRestResources, getRestResource, logger, cancellationToken);
    }
}
