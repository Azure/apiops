using common;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using MoreLinq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class Api
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var configurationApis = GetConfigurationApis(configurationJson);

        await GetApisFromFiles(files, serviceDirectory)
                .LeftJoin(configurationApis,
                          firstKeySelector: api => api.ApiName,
                          secondKeySelector: configurationArtifact => configurationArtifact.ApiName,
                          firstSelector: api => (api.ApiName, api.InformationFile, api.SpecificationFile, ConfigurationApiJson: (JsonObject?)null),
                          bothSelector: (file, configurationArtifact) => (file.ApiName, file.InformationFile, file.SpecificationFile, ConfigurationApiJson: configurationArtifact.Json))
                .ForEachParallel(async artifact => await ProcessDeletedApi(artifact.ApiName, artifact.InformationFile, artifact.SpecificationFile, artifact.ConfigurationApiJson, serviceDirectory, serviceUri, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ApiName ApiName, JsonObject Json)> GetConfigurationApis(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("apis")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(apiJsonObject =>
                                {
                                    var name = apiJsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (ApiName, JsonObject)?
                                            : (new ApiName(name), apiJsonObject);
                                });
    }

    private static IEnumerable<(ApiName ApiName, ApiInformationFile? InformationFile, ApiSpecificationFile? SpecificationFile)> GetApisFromFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        var informationFiles = files.Choose(file => TryGetInformationFile(file, serviceDirectory))
                                    .Select(file => (ApiName: GetApiName(file), File: file));

        var specificationFiles = files.Choose(file => TryGetApiSpecificationFile(file, serviceDirectory))
                                      .Select(specificationFile => (ApiName: GetApiName(specificationFile), SpecificationFile: specificationFile));

        return informationFiles.FullJoin(specificationFiles,
                                         firstKeySelector: informationFile => informationFile.ApiName,
                                         secondKeySelector: specificationFile => specificationFile.ApiName,
                                         firstSelector: informationFile => (informationFile.ApiName, (ApiInformationFile?)informationFile.File, (ApiSpecificationFile?)null),
                                         secondSelector: schema => (schema.ApiName, null, schema.SpecificationFile),
                                         bothSelector: (informationFile, specificationFile) => (informationFile.ApiName, informationFile.File, specificationFile.SpecificationFile));
    }

    private static ApiInformationFile? TryGetInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ApiInformationFile.Name) is false)
        {
            return null;
        }

        var apiDirectory = TryGetApiDirectory(file.Directory, serviceDirectory);

        return apiDirectory is null
                ? null
                : new ApiInformationFile(apiDirectory);
    }

    public static ApiDirectory? TryGetApiDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var apisDirectory = TryGetApisDirectory(directory.Parent, serviceDirectory);
        var apiName = new ApiName(directory.Name);

        return apisDirectory is null
                ? null
                : new ApiDirectory(apiName, apisDirectory);
    }

    private static ApisDirectory? TryGetApisDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
                || directory.Name.Equals(ApisDirectory.Name) is false
                || serviceDirectory.PathEquals(directory.Parent) is false
               ? null
               : new ApisDirectory(serviceDirectory);
    }

    private static ApiName GetApiName(ApiInformationFile file)
    {
        return new ApiName(file.ApiDirectory.GetName());
    }

    private static ApiSpecificationFile? TryGetApiSpecificationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null)
        {
            return null;
        }

        var apiDirectory = TryGetApiDirectory(file.Directory, serviceDirectory);
        if (apiDirectory is null)
        {
            return null;
        }

        if (file.Name.Equals(ApiSpecificationFile.GraphQl.Name))
        {
            return new ApiSpecificationFile.GraphQl(apiDirectory);
        }
        else if (file.Name.Equals(ApiSpecificationFile.Wsdl.Name))
        {
            return new ApiSpecificationFile.Wsdl(apiDirectory);
        }
        else if (file.Name.Equals(ApiSpecificationFile.Wadl.Name))
        {
            return new ApiSpecificationFile.Wadl(apiDirectory);
        }
        else
        {
            return TryGetOpenApiSpecificationFile(file, apiDirectory);
        }
    }

    private static ApiSpecificationFile.OpenApi? TryGetOpenApiSpecificationFile(FileInfo file, ApiDirectory apiDirectory)
    {
        var version = TryGetOpenApiSpecVersion(file);
        if (version is null)
        {
            return null;
        }

        var format = TryGetOpenApiFormat(file);
        if (format is null)
        {
            return null;
        }

        var formatFileName = GetSpecificationFileName(format.Value);
        return file.Name.Equals(formatFileName)
                ? new ApiSpecificationFile.OpenApi(version.Value, format.Value, apiDirectory)
                : null;
    }

    private static OpenApiSpecVersion? TryGetOpenApiSpecVersion(FileInfo file)
    {
        try
        {
            var _ = new OpenApiStreamReader().Read(file.OpenRead(), out var diagnostic);
            return diagnostic.SpecificationVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OpenApiFormat? TryGetOpenApiFormat(FileInfo file)
    {
        return file.Extension switch
        {
            ".json" => OpenApiFormat.Json,
            ".yaml" => OpenApiFormat.Yaml,
            ".yml" => OpenApiFormat.Yaml,
            _ => null
        };
    }

    private static string GetSpecificationFileName(OpenApiFormat format) =>
        format switch
        {
            OpenApiFormat.Json => "specification.json",
            OpenApiFormat.Yaml => "specification.yaml",
            _ => throw new NotSupportedException()
        };

    private static ApiName GetApiName(ApiSpecificationFile specificationFile)
    {
        return new(specificationFile.ApiDirectory.GetName());
    }

    private static async ValueTask ProcessDeletedApi(ApiName apiName, ApiInformationFile? deletedApiInformationFile, ApiSpecificationFile? deletedSpecificationFile, JsonObject? configurationApiJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        switch (deletedApiInformationFile, deletedSpecificationFile)
        {
            // Nothing was deleted
            case (null, null):
                return;
            // Only specificationFile file was deleted, put API with existing information file
            case (null, not null):
                var existingInformationFile = TryGetExistingInformationFile(deletedSpecificationFile.ApiDirectory);
                if (existingInformationFile is null)
                {
                    await Delete(apiName, serviceUri, deleteRestResource, logger, cancellationToken);
                }
                else
                {
                    await PutApi(apiName, existingInformationFile, specificationFile: null, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                }

                return;
            // Only information file was deleted, put API with existing specification file
            case (not null, null):
                var existingSpecificationFile = TryGetExistingSpecificationFile(deletedApiInformationFile.ApiDirectory, serviceDirectory);
                if (existingSpecificationFile is null)
                {
                    await Delete(apiName, serviceUri, deleteRestResource, logger, cancellationToken);
                }
                else
                {
                    await PutApi(apiName, apiInformationFile: null, existingSpecificationFile, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                }

                return;
            // Both information and schema file were deleted, delete API.
            case (not null, not null):
                await Delete(apiName, serviceUri, deleteRestResource, logger, cancellationToken);
                return;
        }
    }

    private static ApiInformationFile? TryGetExistingInformationFile(ApiDirectory apiDirectory)
    {
        var file = new ApiInformationFile(apiDirectory);
        return file.Exists() ? file : null;
    }

    private static async ValueTask Delete(ApiName apiName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting API {apiName}...", apiName);

        var apiUri = GetApiUri(apiName, serviceUri);
        await deleteRestResource(apiUri.Uri, cancellationToken);
    }

    public static ApiUri GetApiUri(ApiName apiName, ServiceUri serviceUri)
    {
        var apisUri = new ApisUri(serviceUri);
        return new ApiUri(apiName, apisUri);
    }

    private static ApiSpecificationFile? TryGetExistingSpecificationFile(ApiDirectory apiDirectory, ServiceDirectory serviceDirectory)
    {
        return apiDirectory.DirectoryExists() ? apiDirectory.GetDirectoryInfo()
                           .EnumerateFiles()
                           .Choose(file => TryGetApiSpecificationFile(file, serviceDirectory))
                           .FirstOrDefault() : null;
    }

    private static async ValueTask PutApi(ApiName apiName, ApiInformationFile? apiInformationFile, ApiSpecificationFile? specificationFile, JsonObject? configurationApiJson, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        if (apiInformationFile is null && specificationFile is null && configurationApiJson is null)
        {
            return;
        }

        logger.LogInformation("Putting API {apiName}...", apiName);

        var apiUri = GetApiUri(apiName, serviceUri);
        var apiJson = await GetApiJson(apiName, apiInformationFile, specificationFile, configurationApiJson, cancellationToken);
        await putRestResource(apiUri.Uri, apiJson, cancellationToken);

        // Handle GraphQL specification
        if (specificationFile is ApiSpecificationFile.GraphQl graphQlSpecificationFile)
        {
            await PutGraphQlSchema(apiUri, graphQlSpecificationFile, putRestResource, cancellationToken);
        }
    }

    private static async ValueTask<JsonObject> GetApiJson(ApiName apiName, ApiInformationFile? apiInformationFile, ApiSpecificationFile? specificationFile, JsonObject? configurationApiJson, CancellationToken cancellationToken)
    {
        var apiJson = new JsonObject();

        if (apiInformationFile is not null)
        {
            var fileJson = apiInformationFile.ReadAsJsonObject();
            apiJson = apiJson.Merge(fileJson);
        }

        if (specificationFile is not null and (ApiSpecificationFile.Wadl or ApiSpecificationFile.Wsdl or ApiSpecificationFile.OpenApi))
        {
            var specificationJson = await GetApiSpecificationJson(specificationFile, cancellationToken);
            apiJson = apiJson.Merge(specificationJson);
        }

        if (configurationApiJson is not null)
        {
            apiJson = apiJson.Merge(configurationApiJson);
        }

        if (apiJson.Any() is false)
        {
            throw new InvalidOperationException($"API {apiName} has an empty JSON object.");
        }

        return apiJson;
    }

    private static async ValueTask<JsonObject> GetApiSpecificationJson(ApiSpecificationFile specificationFile, CancellationToken cancellationToken)
    {
        return new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["format"] = specificationFile switch
                {
                    ApiSpecificationFile.Wadl => "wadl-xml",
                    ApiSpecificationFile.Wsdl => "wsdl",
                    ApiSpecificationFile.OpenApi => "openapi",
                    _ => throw new NotSupportedException()
                },
                ["value"] = specificationFile switch
                {
                    ApiSpecificationFile.Wadl => await specificationFile.ReadAsString(cancellationToken),
                    ApiSpecificationFile.Wsdl => await specificationFile.ReadAsString(cancellationToken),
                    ApiSpecificationFile.OpenApi openApiSpecificationFile => await GetOpenApiV3SpecificationText(openApiSpecificationFile),
                    _ => throw new NotSupportedException()
                }
            }
        };
    }

    private static async ValueTask<string> GetOpenApiV3SpecificationText(ApiSpecificationFile.OpenApi openApiSpecificationFile)
    {
        using var fileStream = openApiSpecificationFile.ReadAsStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(fileStream);
        return readResult.OpenApiDocument.Serialize(openApiSpecificationFile.Version, openApiSpecificationFile.Format);
    }

    private static async ValueTask PutGraphQlSchema(ApiUri apiUri, ApiSpecificationFile.GraphQl graphQlSpecificationFile, PutRestResource putRestResource, CancellationToken cancellationToken)
    {
        var schemasUri = new ApiSchemasUri(apiUri);
        var schemaUri = new ApiSchemaUri(ApiSchemaName.GraphQl, schemasUri);
        var json = new JsonObject()
        {
            ["properties"] = new JsonObject
            {
                ["contentType"] = "application/vnd.ms-azure-apim.graphql.schema",
                ["document"] = new JsonObject()
                {
                    ["value"] = await graphQlSpecificationFile.ReadAsString(cancellationToken)
                }
            }
        };

        await putRestResource(schemaUri.Uri, json, cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var configurationApis = GetConfigurationApis(configurationJson);

        await GetApisFromFiles(files, serviceDirectory)
                .LeftJoin(configurationApis,
                          firstKeySelector: api => api.ApiName,
                          secondKeySelector: configurationArtifact => configurationArtifact.ApiName,
                          firstSelector: api => (api.ApiName, api.InformationFile, api.SpecificationFile, ConfigurationApiJson: (JsonObject?)null),
                          bothSelector: (fileApi, configurationArtifact) => (fileApi.ApiName, fileApi.InformationFile, fileApi.SpecificationFile, ConfigurationApiJson: configurationArtifact.Json))
                .ForEachParallel(async api => await PutApi(api.ApiName, api.InformationFile, api.SpecificationFile, api.ConfigurationApiJson, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }
}