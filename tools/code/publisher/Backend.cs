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

internal static class Backend
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetBackendInformationFiles(files, serviceDirectory)
                .Select(GetBackendName)
                .ForEachParallel(async backendName => await Delete(backendName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<BackendInformationFile> GetBackendInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetBackendInformationFile(file, serviceDirectory));
    }

    private static BackendInformationFile? TryGetBackendInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(BackendInformationFile.Name) is false)
        {
            return null;
        }

        var backendDirectory = TryGetBackendDirectory(file.Directory, serviceDirectory);

        return backendDirectory is null
                ? null
                : new BackendInformationFile(backendDirectory);
    }

    private static BackendDirectory? TryGetBackendDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var backendsDirectory = TryGetBackendsDirectory(directory.Parent, serviceDirectory);
        if (backendsDirectory is null)
        {
            return null;
        }

        var backendName = new BackendName(directory.Name);
        return new BackendDirectory(backendName, backendsDirectory);
    }

    private static BackendsDirectory? TryGetBackendsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(BackendsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new BackendsDirectory(serviceDirectory);
    }

    private static BackendName GetBackendName(BackendInformationFile file)
    {
        return new(file.BackendDirectory.GetName());
    }

    private static async ValueTask Delete(BackendName backendName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetBackendUri(backendName, serviceUri);

        logger.LogInformation("Deleting backend {backendName}...", backendName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static BackendUri GetBackendUri(BackendName backendName, ServiceUri serviceUri)
    {
        var backendsUri = new BackendsUri(serviceUri);
        return new BackendUri(backendName, backendsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutBackend(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(BackendName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationBackends(configurationJson);

        return GetBackendInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetBackendName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(BackendName Name, JsonObject Json)> GetConfigurationBackends(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("backends")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (BackendName, JsonObject)?
                                            : (new BackendName(name), jsonObject);
                                });
    }

    private static async ValueTask PutBackend(BackendName backendName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting backend {backendName}...", backendName);

        var uri = GetBackendUri(backendName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}