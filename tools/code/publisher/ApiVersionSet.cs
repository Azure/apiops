using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ApiVersionSet
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetApiVersionSetInformationFiles(files, serviceDirectory)
                .Select(GetApiVersionSetName)
                .ForEachParallel(async apiVersionSetName => await Delete(apiVersionSetName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ApiVersionSetInformationFile> GetApiVersionSetInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetApiVersionSetInformationFile(file, serviceDirectory));
    }

    private static ApiVersionSetInformationFile? TryGetApiVersionSetInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ApiVersionSetInformationFile.Name) is false)
        {
            return null;
        }

        var apiVersionSetDirectory = TryGetApiVersionSetDirectory(file.Directory, serviceDirectory);

        return apiVersionSetDirectory is null
                ? null
                : new ApiVersionSetInformationFile(apiVersionSetDirectory);
    }

    private static ApiVersionSetDirectory? TryGetApiVersionSetDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var apiVersionSetsDirectory = TryGetApiVersionSetsDirectory(directory.Parent, serviceDirectory);
        if (apiVersionSetsDirectory is null)
        {
            return null;
        }

        var apiVersionSetName = new ApiVersionSetName(directory.Name);
        return new ApiVersionSetDirectory(apiVersionSetName, apiVersionSetsDirectory);
    }

    private static ApiVersionSetsDirectory? TryGetApiVersionSetsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(ApiVersionSetsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new ApiVersionSetsDirectory(serviceDirectory);
    }

    private static ApiVersionSetName GetApiVersionSetName(ApiVersionSetInformationFile file)
    {
        return new(file.ApiVersionSetDirectory.GetName());
    }

    private static async ValueTask Delete(ApiVersionSetName apiVersionSetName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetApiVersionSetUri(apiVersionSetName, serviceUri);

        logger.LogInformation("Deleting apiVersionSet {apiVersionSetName}...", apiVersionSetName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static ApiVersionSetUri GetApiVersionSetUri(ApiVersionSetName apiVersionSetName, ServiceUri serviceUri)
    {
        var apiVersionSetsUri = new ApiVersionSetsUri(serviceUri);
        return new ApiVersionSetUri(apiVersionSetName, apiVersionSetsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutApiVersionSet(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ApiVersionSetName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationApiVersionSets(configurationJson);

        return GetApiVersionSetInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetApiVersionSetName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(ApiVersionSetName Name, JsonObject Json)> GetConfigurationApiVersionSets(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("apiVersionSets")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (ApiVersionSetName, JsonObject)?
                                            : (new ApiVersionSetName(name), jsonObject);
                                });
    }

    private static async ValueTask PutApiVersionSet(ApiVersionSetName apiVersionSetName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting apiVersionSet {apiVersionSetName}...", apiVersionSetName);

        var uri = GetApiVersionSetUri(apiVersionSetName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}