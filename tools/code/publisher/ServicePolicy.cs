using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ServicePolicy
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetPolicyFiles(files, serviceDirectory)
                .Select(GetPolicyName)
                .ForEachParallel(async policyName => await Delete(policyName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ServicePolicyFile> GetPolicyFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetServicePolicyFile(file, serviceDirectory));
    }

    private static ServicePolicyFile? TryGetServicePolicyFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null
            || file.Name.EndsWith("xml") is false
            || serviceDirectory.PathEquals(file.Directory) is false)
        {
            return null;
        }

        var policyNameString = Path.GetFileNameWithoutExtension(file.FullName);
        var policyName = new ServicePolicyName(policyNameString);
        return new ServicePolicyFile(policyName, serviceDirectory);
    }

    private static ServicePolicyName GetPolicyName(ServicePolicyFile file)
    {
        return new(file.GetNameWithoutExtensions());
    }

    private static async ValueTask Delete(ServicePolicyName policyName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting service policy {policyName}...", policyName);

        var policyUri = GetServicePolicyUri(policyName, serviceUri);
        await deleteRestResource(policyUri.Uri, cancellationToken);
    }

    private static ServicePolicyUri GetServicePolicyUri(ServicePolicyName policyName, ServiceUri serviceUri)
    {
        var policiesUri = new ServicePoliciesUri(serviceUri);
        return new ServicePolicyUri(policyName, policiesUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory, cancellationToken)
                .ForEachParallel(async artifact => await PutPolicy(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static async IAsyncEnumerable<(ServicePolicyName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileArtifacts = await GetFilePolicies(files, serviceDirectory, cancellationToken).ToListAsync(cancellationToken);
        var configurationArtifacts = GetConfigurationPolicies(configurationJson);
        var artifacts = fileArtifacts.LeftJoin(configurationArtifacts,
                                               keySelector: artifact => artifact.Name,
                                               bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));

        foreach (var artifact in artifacts)
        {
            yield return artifact;
        }
    }

    private static IAsyncEnumerable<(ServicePolicyName Name, JsonObject Json)> GetFilePolicies(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        return GetPolicyFiles(files, serviceDirectory)
                .ToAsyncEnumerable()
                .SelectAwaitWithCancellation(async (file, token) =>
                {
                    var policyText = await file.ReadAsString(cancellationToken);
                    var policyJson = new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["format"] = "rawxml",
                            ["value"] = policyText
                        }
                    };
                    return (GetPolicyName(file), policyJson);
                });
    }

    private static IEnumerable<(ServicePolicyName Name, JsonObject Json)> GetConfigurationPolicies(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("policies")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(policyJsonObject =>
                                {
                                    var name = policyJsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (ServicePolicyName, JsonObject)?
                                            : (new ServicePolicyName(name), policyJsonObject);
                                });
    }

    private static async ValueTask PutPolicy(ServicePolicyName policyName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting service policy {policyName}...", policyName);

        var policyUri = GetServicePolicyUri(policyName, serviceUri);
        await putRestResource(policyUri.Uri, json, cancellationToken);
    }
}