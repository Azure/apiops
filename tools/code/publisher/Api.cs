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
                          firstSelector: api => (api.ApiName, api.InformationFile, api.Schema, ConfigurationApiJson: (JsonObject?)null),
                          bothSelector: (file, configurationArtifact) => (file.ApiName, file.InformationFile, file.Schema, ConfigurationApiJson: configurationArtifact.Json))
                .ForEachParallel(async artifact => await ProcessDeletedApi(artifact.ApiName, artifact.InformationFile, artifact.Schema, artifact.ConfigurationApiJson, serviceDirectory, serviceUri, putRestResource, deleteRestResource, logger, cancellationToken),
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

    private static IEnumerable<(ApiName ApiName, ApiInformationFile? InformationFile, Schema? Schema)> GetApisFromFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        var informationFiles = files.Choose(file => TryGetInformationFile(file, serviceDirectory))
                                    .Select(file => (ApiName: GetApiName(file), File: file));

        var schemas = files.Choose(file => TryGetSchema(file, serviceDirectory))
                           .Select(schema => (ApiName: GetApiName(schema), Schema: schema));

        return informationFiles.FullJoin(schemas,
                                         firstKeySelector: informationFile => informationFile.ApiName,
                                         secondKeySelector: schema => schema.ApiName,
                                         firstSelector: informationFile => (informationFile.ApiName, (ApiInformationFile?)informationFile.File, (Schema?)null),
                                         secondSelector: schema => (schema.ApiName, null, schema.Schema),
                                         bothSelector: (informationFile, schema) => (informationFile.ApiName, informationFile.File, schema.Schema));
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

    private static Schema? TryGetSchema(FileInfo file, ServiceDirectory serviceDirectory)
    {
        var graphQlSchemaFile = TryGetGraphQlSchemaFile(file, serviceDirectory);
        if (graphQlSchemaFile is not null)
        {
            return new Schema.GraphQl(graphQlSchemaFile);
        }

        var specificationFile = TryGetSpecificationFile(file, serviceDirectory);
        if (specificationFile is not null)
        {
            return new Schema.OpenApi(specificationFile);
        }

        return null;
    }

    private static GraphQlSchemaFile? TryGetGraphQlSchemaFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(GraphQlSchemaFile.Name) is false)
        {
            return null;
        }

        var apiDirectory = TryGetApiDirectory(file.Directory, serviceDirectory);
        if (apiDirectory is null)
        {
            return null;
        }

        return new GraphQlSchemaFile(apiDirectory);
    }

    private static ApiSpecificationFile? TryGetSpecificationFile(FileInfo? file, ServiceDirectory serviceDirectory)
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
        if (formatFileName.Equals(file.Name) is false)
        {
            return null;
        }

        return new ApiSpecificationFile(version.Value, format.Value, apiDirectory);
    }

    private static OpenApiSpecVersion? TryGetOpenApiSpecVersion(FileInfo? file)
    {
        try
        {
            if (file is null)
            {
                return null;
            }

            var _ = new OpenApiStreamReader().Read(file.OpenRead(), out var diagnostic);
            return diagnostic.SpecificationVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OpenApiFormat? TryGetOpenApiFormat(FileInfo? file)
    {
        return file?.Extension switch
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

    private static ApiName GetApiName(Schema schema)
    {
        return schema switch
        {
            Schema.GraphQl graphQl => new(graphQl.File.ApiDirectory.GetName()),
            Schema.OpenApi openApi => new(openApi.File.ApiDirectory.GetName()),
            var unsupportedSchema => throw new NotSupportedException($"Cannot get API name from files of type {unsupportedSchema.GetType()}.")
        };
    }

    private static async ValueTask ProcessDeletedApi(ApiName apiName, ApiInformationFile? deletedApiInformationFile, Schema? deletedSchema, JsonObject? configurationApiJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        switch (deletedApiInformationFile, deletedSchema)
        {
            // Nothing was deleted
            case (null, null):
                return;
            // Only schema file was deleted, put API with existing information file
            case (null, not null):
                var existingInformationFile = TryGetExistingInformationFile(deletedSchema.GetApiDirectory());
                if (existingInformationFile is not null)
                {
                    await PutApi(apiName, existingInformationFile, specificationFile: null, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                }

                return;
            // Only information file was deleted, put API with existing specification file
            case (not null, null):
                var existingSpecificationFile = TryGetExistingSpecificationFile(deletedApiInformationFile.ApiDirectory, serviceDirectory);
                if (existingSpecificationFile is not null)
                {
                    await PutApi(apiName, apiInformationFile: null, existingSpecificationFile, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                }

                return;
            // Both information and schema file was deleted, delete API.
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

    public static ApiUri GetApiUri(ApiName apiName, ServiceUri serviceUri)
    {
        var apisUri = new ApisUri(serviceUri);
        return new ApiUri(apiName, apisUri);
    }

    private static ApiSpecificationFile? TryGetExistingSpecificationFile(ApiDirectory apiDirectory, ServiceDirectory serviceDirectory)
    {
        return apiDirectory.GetDirectoryInfo()
                           .EnumerateFiles()
                           .Choose(file => TryGetSpecificationFile(file, serviceDirectory))
                           .FirstOrDefault();
    }

    private static async ValueTask PutApi(ApiName apiName, ApiInformationFile? apiInformationFile, ApiSpecificationFile? specificationFile, JsonObject? configurationApiJson, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        if (apiInformationFile is null && specificationFile is null && configurationApiJson is null)
        {
            return;
        }

        logger.LogInformation("Putting API {apiName}...", apiName);

        var apiUri = GetApiUri(apiName, serviceUri);

        var apiJson = new JsonObject();

        if (apiInformationFile is not null)
        {
            var fileJson = apiInformationFile.ReadAsJsonObject();
            apiJson = apiJson.Merge(fileJson);
        }

        if (specificationFile is not null)
        {
            var fileJson = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["format"] = "openapi",
                    ["value"] = await GetOpenApiV3SpecificationText(specificationFile)
                }
            };
            apiJson = apiJson.Merge(fileJson);
        }

        if (configurationApiJson is not null)
        {
            apiJson = apiJson.Merge(configurationApiJson);
        }

        await putRestResource(apiUri.Uri, apiJson, cancellationToken);
    }

    private static async ValueTask<string> GetOpenApiV3SpecificationText(ApiSpecificationFile specificationFile)
    {
        using var fileStream = specificationFile.ReadAsStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(fileStream);
        return readResult.OpenApiDocument.Serialize(specificationFile.Version, specificationFile.Format);
    }

    private static async ValueTask Delete(ApiName apiName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting API {apiName}...", apiName);

        var apiUri = GetApiUri(apiName, serviceUri);
        await deleteRestResource(apiUri.Uri, cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var configurationApis = GetConfigurationApis(configurationJson);

        await GetApisFromFiles(files, serviceDirectory)
                .LeftJoin(configurationApis,
                          firstKeySelector: api => api.ApiName,
                          secondKeySelector: configurationArtifact => configurationArtifact.ApiName,
                          firstSelector: api => (api.ApiName, api.InformationFile, api.Schema, ConfigurationApiJson: (JsonObject?)null),
                          bothSelector: (file, configurationArtifact) => (file.ApiName, file.InformationFile, file.Schema, ConfigurationApiJson: configurationArtifact.Json))
                .ForEachParallel(async artifact => await PutApi(artifact.ApiName, artifact.InformationFile, artifact.Schema, artifact.ConfigurationApiJson, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static async ValueTask PutApi(ApiName apiName, ApiInformationFile? apiInformationFile, Schema? schema, JsonObject? configurationApiJson, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        switch (apiInformationFile, schema)
        {
            case (not null, Schema.OpenApi openApiSchemaFile):
                await PutApi(apiName, apiInformationFile, openApiSchemaFile.File, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                return;
            case (not null, Schema.GraphQl graphQlSchemaFile):
                await PutApi(apiName, apiInformationFile, specificationFile: null, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                await PutGraphQlSchema(apiName, graphQlSchemaFile.File, serviceUri, putRestResource, logger, cancellationToken);
                return;
            case (null, Schema.OpenApi openApiSchemaFile):
                await PutApi(apiName, apiInformationFile: null, openApiSchemaFile.File, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken);
                return;
        }
    }

    private static async ValueTask PutGraphQlSchema(ApiName apiName, GraphQlSchemaFile graphQlSchemaFile, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting GraphQL schema for API {apiName}...", apiName);

        var apiUri = GetApiUri(apiName, serviceUri);
        var schemasUri = new ApiSchemasUri(apiUri);
        var schemaUri = new ApiSchemaUri(ApiSchemaName.GraphQl, schemasUri);
        var json = new JsonObject()
        {
            ["properties"] = new JsonObject
            {
                ["contentType"] = "application/vnd.ms-azure-apim.graphql.schema",
                ["document"] = new JsonObject()
                {
                    ["value"] = await graphQlSchemaFile.ReadAsString(cancellationToken)
                }
            }
        };

        await putRestResource(schemaUri.Uri, json, cancellationToken);
    }

    private abstract record Schema
    {
        public record GraphQl(GraphQlSchemaFile File) : Schema { }

        public record OpenApi(ApiSpecificationFile File) : Schema { }

        public ApiDirectory GetApiDirectory()
        {
            return this switch
            {
                GraphQl file => file.File.ApiDirectory,
                OpenApi file => file.File.ApiDirectory,
                _ => throw new NotSupportedException()
            };
        }
    }
}