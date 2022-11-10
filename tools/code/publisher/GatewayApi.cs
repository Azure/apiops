using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class GatewayApi
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }

    private static async ValueTask Put(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactGatewayApis(files, configurationJson, serviceDirectory)
                .ForEachParallel(async gateway => await Put(gateway.GatewayName, gateway.ApiNames, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(GatewayName GatewayName, ImmutableList<ApiName> ApiNames)> GetArtifactGatewayApis(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationGatewayApis(configurationJson);

        return GetGatewayApisFiles(files, serviceDirectory)
                .Select(file =>
                {
                    var gatewayName = GetGatewayName(file);
                    var apiNames = file.ReadAsJsonArray()
                                    .Choose(node => node as JsonObject)
                                    .Choose(apiJsonObject => apiJsonObject.TryGetStringProperty("name"))
                                    .Select(name => new ApiName(name))
                                    .ToImmutableList();
                    return (GatewayName: gatewayName, ApiNames: apiNames);
                })
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.GatewayName,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.GatewayName, configurationArtifact.ApiNames));
    }

    private static IEnumerable<GatewayApisFile> GetGatewayApisFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetApisFile(file, serviceDirectory));
    }

    private static GatewayApisFile? TryGetApisFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(GatewayApisFile.Name) is false)
        {
            return null;
        }

        var gatewayDirectory = Gateway.TryGetGatewayDirectory(file.Directory, serviceDirectory);

        return gatewayDirectory is null
                ? null
                : new GatewayApisFile(gatewayDirectory);
    }

    private static GatewayName GetGatewayName(GatewayApisFile file)
    {
        return new(file.GatewayDirectory.GetName());
    }

    private static IEnumerable<(GatewayName GatewayName, ImmutableList<ApiName> ApiNames)> GetConfigurationGatewayApis(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("gateways")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose<JsonObject, (GatewayName GatewayName, ImmutableList<ApiName> ApiNames)>(gatewayJsonObject =>
                                {
                                    var gatewayNameString = gatewayJsonObject.TryGetStringProperty("name");
                                    if (gatewayNameString is null)
                                    {
                                        return default;
                                    }

                                    var gatewayName = new GatewayName(gatewayNameString);

                                    var apisJsonArray = gatewayJsonObject.TryGetJsonArrayProperty("apis");
                                    if (apisJsonArray is null)
                                    {
                                        return default;
                                    }

                                    if (apisJsonArray.Any() is false)
                                    {
                                        return (gatewayName, ImmutableList.Create<ApiName>());
                                    }

                                    // If APIs are defined in configuration but none have a 'name' property, skip this resource
                                    var apiNames = apisJsonArray.Choose(node => node as JsonObject)
                                                                .Choose(apiJsonObject => apiJsonObject.TryGetStringProperty("name"))
                                                                .Select(name => new ApiName(name))
                                                                .ToImmutableList();
                                    return apiNames.Any() ? (gatewayName, apiNames) : default;
                                });
    }

    private static async ValueTask Put(GatewayName gatewayName, IReadOnlyCollection<ApiName> apiNames, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var gatewayUri = GetGatewayUri(gatewayName, serviceUri);
        var gatewayApisUri = new GatewayApisUri(gatewayUri);

        var existingApiNames = await listRestResources(gatewayApisUri.Uri, cancellationToken)
                                        .Select(apiJsonObject => apiJsonObject.GetStringProperty("name"))
                                        .Select(name => new ApiName(name))
                                        .ToListAsync(cancellationToken);

        var apiNamesToPut = apiNames.Except(existingApiNames);
        var apiNamesToRemove = existingApiNames.Except(apiNames);

        await apiNamesToRemove.ForEachParallel(async apiName =>
        {
            logger.LogInformation("Removing API {apiName} in gateway {gatewayName}...", apiName, gatewayName);
            await Delete(apiName, gatewayUri, deleteRestResource, cancellationToken);
        }, cancellationToken);

        await apiNamesToPut.ForEachParallel(async apiName =>
        {
            logger.LogInformation("Putting API {apiName} in gateway {gatewayName}...", apiName, gatewayName);
            await Put(apiName, gatewayUri, putRestResource, cancellationToken);
        }, cancellationToken);
    }

    private static GatewayUri GetGatewayUri(GatewayName gatewayName, ServiceUri serviceUri)
    {
        var gatewaysUri = new GatewaysUri(serviceUri);
        return new GatewayUri(gatewayName, gatewaysUri);
    }

    private static async ValueTask Delete(ApiName apiName, GatewayUri gatewayUri, DeleteRestResource deleteRestResource, CancellationToken cancellationToken)
    {
        var gatewayApisUri = new GatewayApisUri(gatewayUri);
        var gatewayApiUri = new GatewayApiUri(apiName, gatewayApisUri);

        await deleteRestResource(gatewayApiUri.Uri, cancellationToken);
    }

    private static async ValueTask Put(ApiName apiName, GatewayUri gatewayUri, PutRestResource putRestResource, CancellationToken cancellationToken)
    {
        var gatewayApisUri = new GatewayApisUri(gatewayUri);
        var gatewayApiUri = new GatewayApiUri(apiName, gatewayApisUri);

        await putRestResource(gatewayApiUri.Uri, new JsonObject(), cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }
}