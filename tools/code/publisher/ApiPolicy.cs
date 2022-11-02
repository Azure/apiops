using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ApiPolicy
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetPolicyFiles(files, serviceDirectory)
                .Select(file => (PolicyName: GetPolicyName(file), ApiName: GetApiName(file)))
                .ForEachParallel(async policy => await Delete(policy.PolicyName, policy.ApiName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ApiPolicyFile> GetPolicyFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetApiPolicyFile(file, serviceDirectory));
    }

    private static ApiPolicyFile? TryGetApiPolicyFile(FileInfo file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.EndsWith("xml") is false)
        {
            return null;
        }

        var apiDirectory = Api.TryGetApiDirectory(file.Directory, serviceDirectory);
        if (apiDirectory is null)
        {
            return null;
        }

        var policyNameString = Path.GetFileNameWithoutExtension(file.FullName);
        var policyName = new ApiPolicyName(policyNameString);
        return new ApiPolicyFile(policyName, apiDirectory);
    }

    private static ApiPolicyName GetPolicyName(ApiPolicyFile file)
    {
        return new(file.GetNameWithoutExtensions());
    }

    private static ApiName GetApiName(ApiPolicyFile file)
    {
        return new(file.ApiDirectory.GetName());
    }

    private static async ValueTask Delete(ApiPolicyName policyName, ApiName apiName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy {policyName} in API {apiName}...", policyName, apiName);

        var uri = GetApiPolicyUri(policyName, apiName, serviceUri);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static ApiPolicyUri GetApiPolicyUri(ApiPolicyName policyName, ApiName apiName, ServiceUri serviceUri)
    {
        var apiUri = Api.GetApiUri(apiName, serviceUri);
        var policiesUri = new ApiPoliciesUri(apiUri);
        return new ApiPolicyUri(policyName, policiesUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory, cancellationToken)
                .ForEachParallel(async artifact => await PutPolicy(artifact.PolicyName, artifact.ApiName, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static async IAsyncEnumerable<(ApiPolicyName PolicyName, ApiName ApiName, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileArtifacts = await GetFilePolicies(files, serviceDirectory, cancellationToken).ToListAsync(cancellationToken);
        var configurationArtifacts = GetConfigurationPolicies(configurationJson);
        var artifacts = fileArtifacts.LeftJoin(configurationArtifacts,
                                               keySelector: artifact => (artifact.PolicyName, artifact.ApiName),
                                               bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.PolicyName, fileArtifact.ApiName, fileArtifact.Json.Merge(configurationArtifact.Json)));

        foreach (var artifact in artifacts)
        {
            yield return artifact;
        }
    }

    private static IAsyncEnumerable<(ApiPolicyName PolicyName, ApiName ApiName, JsonObject Json)> GetFilePolicies(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
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
                    return (GetPolicyName(file), GetApiName(file), policyJson);
                });
    }

    private static IEnumerable<(ApiPolicyName PolicyName, ApiName ApiName, JsonObject Json)> GetConfigurationPolicies(JsonObject configurationJson)
    {
        return GetConfigurationApis(configurationJson)
                .SelectMany(api => GetConfigurationPolicies(api.ApiName, api.Json));
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

    private static IEnumerable<(ApiPolicyName PolicyName, ApiName ApiName, JsonObject Json)> GetConfigurationPolicies(ApiName apiName, JsonObject configurationOperationJson)
    {
        return configurationOperationJson.TryGetJsonArrayProperty("policies")
                                         .IfNullEmpty()
                                         .Choose(node => node as JsonObject)
                                         .Choose(policyJsonObject =>
                                         {
                                             var name = policyJsonObject.TryGetStringProperty("name");
                                             return name is null
                                                     ? null as (ApiPolicyName, ApiName, JsonObject)?
                                                     : (new ApiPolicyName(name), apiName, policyJsonObject);
                                         });
    }

    private static async ValueTask PutPolicy(ApiPolicyName policyName, ApiName apiName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy {policyName} in api {apiName}...", policyName, apiName);

        var policyUri = GetApiPolicyUri(policyName, apiName, serviceUri);
        await putRestResource(policyUri.Uri, json, cancellationToken);
    }
}