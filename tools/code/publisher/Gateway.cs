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

internal static class Gateway
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetGatewayInformationFiles(files, serviceDirectory)
                .Select(GetGatewayName)
                .ForEachParallel(async gatewayName => await Delete(gatewayName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<GatewayInformationFile> GetGatewayInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetGatewayInformationFile(file, serviceDirectory));
    }

    private static GatewayInformationFile? TryGetGatewayInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(GatewayInformationFile.Name) is false)
        {
            return null;
        }

        var gatewayDirectory = TryGetGatewayDirectory(file.Directory, serviceDirectory);

        return gatewayDirectory is null
                ? null
                : new GatewayInformationFile(gatewayDirectory);
    }

    public static GatewayDirectory? TryGetGatewayDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var gatewaysDirectory = TryGetGatewaysDirectory(directory.Parent, serviceDirectory);
        if (gatewaysDirectory is null)
        {
            return null;
        }

        var gatewayName = new GatewayName(directory.Name);
        return new GatewayDirectory(gatewayName, gatewaysDirectory);
    }

    private static GatewaysDirectory? TryGetGatewaysDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(GatewaysDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new GatewaysDirectory(serviceDirectory);
    }

    private static GatewayName GetGatewayName(GatewayInformationFile file)
    {
        return new(file.GatewayDirectory.GetName());
    }

    private static async ValueTask Delete(GatewayName gatewayName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetGatewayUri(gatewayName, serviceUri);

        logger.LogInformation("Deleting gateway {gatewayName}...", gatewayName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static GatewayUri GetGatewayUri(GatewayName gatewayName, ServiceUri serviceUri)
    {
        var gatewaysUri = new GatewaysUri(serviceUri);
        return new GatewayUri(gatewayName, gatewaysUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutGateway(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(GatewayName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationGateways(configurationJson);

        return GetGatewayInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetGatewayName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(GatewayName Name, JsonObject Json)> GetConfigurationGateways(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("gateways")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (GatewayName, JsonObject)?
                                            : (new GatewayName(name), jsonObject);
                                });
    }

    private static async ValueTask PutGateway(GatewayName gatewayName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting gateway {gatewayName}...", gatewayName);

        var uri = GetGatewayUri(gatewayName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}