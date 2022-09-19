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

        await GetInformationAndSpecificationFiles(files, serviceDirectory)
                .LeftJoin(configurationApis,
                          firstKeySelector: files => files.ApiName,
                          secondKeySelector: configurationArtifact => configurationArtifact.ApiName,
                          firstSelector: files => (files.ApiName, files.InformationFile, files.SpecificationFile, ConfigurationApiJson: (JsonObject?)null),
                          bothSelector: (files, configurationArtifact) => (files.ApiName, files.InformationFile, files.SpecificationFile, ConfigurationApiJson: configurationArtifact.Json))
                .ForEachParallel(async artifact => await ProcessDeletedArtifacts(artifact.ApiName, artifact.InformationFile, artifact.SpecificationFile, artifact.ConfigurationApiJson, serviceDirectory, serviceUri, putRestResource, deleteRestResource, logger, cancellationToken),
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

    private static IEnumerable<(ApiName ApiName, ApiInformationFile? InformationFile, ApiSpecificationFile? SpecificationFile)> GetInformationAndSpecificationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        var informationFiles = files.Choose(file => TryGetInformationFile(file, serviceDirectory))
                                    .Select(file => (ApiName: GetApiName(file), File: file));

        var specificationFiles = files.Choose(file => TryGetSpecificationFile(file, serviceDirectory))
                                      .Select(file => (ApiName: GetApiName(file), File: file));

        return informationFiles.FullJoin(specificationFiles,
                                         firstKeySelector: informationFile => informationFile.ApiName,
                                         secondKeySelector: specificationFile => specificationFile.ApiName,
                                         firstSelector: informationFile => (informationFile.ApiName, (ApiInformationFile?)informationFile.File, (ApiSpecificationFile?)null),
                                         secondSelector: specificationFile => (specificationFile.ApiName, null, specificationFile.File),
                                         bothSelector: (informationFile, specificationFile) => (informationFile.ApiName, informationFile.File, specificationFile.File));
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

    private static ApiName GetApiName(ApiSpecificationFile file)
    {
        return new ApiName(file.ApiDirectory.GetName());
    }

    private static async ValueTask ProcessDeletedArtifacts(ApiName apiName, ApiInformationFile? deletedApiInformationFile, ApiSpecificationFile? deletedApiSpecificationFile, JsonObject? configurationApiJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        switch (deletedApiInformationFile, deletedApiSpecificationFile)
        {
            // Nothing was deleted
            case (null, null):
                return;
            // Only specification file was deleted. If information file still exists, put its contents; otherwise, delete API.
            case (null, _):
                var existingInformationFile = new ApiInformationFile(deletedApiSpecificationFile.ApiDirectory);
                await (existingInformationFile.Exists()
                        ? PutArtifacts(apiName, existingInformationFile, null, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken)
                        : DeleteApi(apiName, serviceUri, deleteRestResource, logger, cancellationToken));
                return;
            // Only information file was deleted. If specification file still exists, put its contents; otherwise, delete API.
            case (_, null):
                var existingSpecificationFile = deletedApiInformationFile.ApiDirectory.EnumerateFilesRecursively()
                                                                                      .Choose(file => TryGetSpecificationFile(file, serviceDirectory))
                                                                                      .FirstOrDefault();
                await (existingSpecificationFile is not null
                        ? PutArtifacts(apiName, null, existingSpecificationFile, configurationApiJson, serviceUri, putRestResource, logger, cancellationToken)
                        : DeleteApi(apiName, serviceUri, deleteRestResource, logger, cancellationToken));
                return;
            // Both were deleted; delete API.
            case (_, _):
                await DeleteApi(apiName, serviceUri, deleteRestResource, logger, cancellationToken);
                return;
        }
    }

    private static async ValueTask PutArtifacts(ApiName apiName, ApiInformationFile? apiInformationFile, ApiSpecificationFile? apiSpecificationFile, JsonObject? configurationApiJson, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiJson = await GetApiJson(apiInformationFile, apiSpecificationFile, configurationApiJson);
        if (apiJson is null)
        {
            return;
        }

        logger.LogInformation("Putting API {apiName}...", apiName);
        var apiUri = GetApiUri(apiName, serviceUri);
        await putRestResource(apiUri.Uri, apiJson, cancellationToken);
    }

    public static ApiUri GetApiUri(ApiName apiName, ServiceUri serviceUri)
    {
        var apisUri = new ApisUri(serviceUri);
        return new ApiUri(apiName, apisUri);
    }

    private static async ValueTask<JsonObject?> GetApiJson(ApiInformationFile? apiInformationFile, ApiSpecificationFile? apiSpecificationFile, JsonObject? configurationApiJson)
    {
        if (apiInformationFile is null && apiSpecificationFile is null && configurationApiJson is null)
        {
            return null;
        }

        var apiJson = apiInformationFile?.ReadAsJsonObject() ?? new JsonObject();
        if (apiSpecificationFile is not null)
        {
            var propertiesJson = apiJson.TryGetJsonObjectProperty("properties") ?? new JsonObject();
            propertiesJson["format"] = "openapi";
            propertiesJson["value"] = await GetOpenApiV3SpecificationText(apiSpecificationFile);
            apiJson["properties"] = propertiesJson;
        }

        if (configurationApiJson is not null)
        {
            apiJson = apiJson.Merge(configurationApiJson);
        }

        return apiJson;
    }

    private static async ValueTask<string> GetOpenApiV3SpecificationText(ApiSpecificationFile specificationFile)
    {
        using var fileStream = specificationFile.ReadAsStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(fileStream);
        return readResult.OpenApiDocument.Serialize(specificationFile.Version, specificationFile.Format);
    }

    private static async ValueTask DeleteApi(ApiName apiName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting API {apiName}...", apiName);

        var apiUri = GetApiUri(apiName, serviceUri);
        await deleteRestResource(apiUri.Uri, cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, bool putAllConfigurationArtifacts, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, putAllConfigurationArtifacts, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutArtifacts(artifact.ApiName, artifact.InformationFile, artifact.SpecificationFile, artifact.ConfigurationApiJson, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ApiName ApiName, ApiInformationFile? InformationFile, ApiSpecificationFile? SpecificationFile, JsonObject? ConfigurationApiJson)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, bool putAllConfigurationArtifacts, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var fileArtifacts = GetInformationAndSpecificationFiles(files, serviceDirectory);
        var configurationApis = GetConfigurationApis(configurationJson);

        return putAllConfigurationArtifacts
                ? fileArtifacts.FullJoin(configurationApis,
                                         firstKeySelector: files => files.ApiName,
                                         secondKeySelector: configurationArtifact => configurationArtifact.ApiName,
                                         firstSelector: files => (files.ApiName, files.InformationFile, files.SpecificationFile, ConfigurationApiJson: (JsonObject?)null),
                                         secondSelector: configurationArtifact => (configurationArtifact.ApiName, null, null, configurationArtifact.Json),
                                         bothSelector: (files, configurationArtifact) => (files.ApiName, files.InformationFile, files.SpecificationFile, ConfigurationApiJson: configurationArtifact.Json))
                : fileArtifacts.LeftJoin(configurationApis,
                                         firstKeySelector: files => files.ApiName,
                                         secondKeySelector: configurationArtifact => configurationArtifact.ApiName,
                                         firstSelector: files => (files.ApiName, files.InformationFile, files.SpecificationFile, ConfigurationApiJson: (JsonObject?)null),
                                         bothSelector: (files, configurationArtifact) => (files.ApiName, files.InformationFile, files.SpecificationFile, ConfigurationApiJson: configurationArtifact.Json));
    }
}