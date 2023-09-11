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

internal static class ApiOperationPolicy
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetPolicyFiles(files, serviceDirectory)
                .Select(file => (PolicyName: GetPolicyName(file), OperationName: GetOperationName(file), ApiName: GetApiName(file)))
                .ForEachParallel(async file => await Delete(file.PolicyName, file.OperationName, file.ApiName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ApiOperationPolicyFile> GetPolicyFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetApiOperationPolicyFile(file, serviceDirectory));
    }

    private static ApiOperationPolicyFile? TryGetApiOperationPolicyFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.EndsWith("xml") is false)
        {
            return null;
        }

        var operationDirectory = ApiOperation.TryGetApiOperationDirectory(file.Directory, serviceDirectory);
        if (operationDirectory is null)
        {
            return null;
        }

        var policyNameString = Path.GetFileNameWithoutExtension(file.FullName);
        var policyName = new ApiOperationPolicyName(policyNameString);
        return new ApiOperationPolicyFile(policyName, operationDirectory);
    }

    private static ApiName GetApiName(ApiOperationPolicyFile file)
    {
        return new(file.ApiOperationDirectory.ApiOperationsDirectory.ApiDirectory.GetName());
    }

    private static ApiOperationName GetOperationName(ApiOperationPolicyFile file)
    {
        return new(file.ApiOperationDirectory.GetName());
    }

    private static ApiOperationPolicyName GetPolicyName(ApiOperationPolicyFile file)
    {
        return new(file.GetNameWithoutExtensions());
    }

    private static async ValueTask Delete(ApiOperationPolicyName policyName, ApiOperationName operationName, ApiName apiName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy {policyName} for operation {operationName} in api {apiName}...", policyName, operationName, apiName);

        var policyUri = GetApiOperationPolicyUri(policyName, operationName, apiName, serviceUri);
        await deleteRestResource(policyUri.Uri, cancellationToken);
    }

    private static ApiOperationPolicyUri GetApiOperationPolicyUri(ApiOperationPolicyName policyName, ApiOperationName operationName, ApiName apiName, ServiceUri serviceUri)
    {
        var policiesUri = GetApiOperationPoliciesUri(operationName, apiName, serviceUri);
        return new ApiOperationPolicyUri(policyName, policiesUri);
    }

    private static ApiOperationPoliciesUri GetApiOperationPoliciesUri(ApiOperationName operationName, ApiName apiName, ServiceUri serviceUri)
    {
        var operationUri = ApiOperation.GetApiOperationUri(operationName, apiName, serviceUri);
        return new ApiOperationPoliciesUri(operationUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory, cancellationToken)
                .ForEachParallel(async artifact => await PutPolicy(artifact.PolicyName, artifact.OperationName, artifact.ApiName, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static async IAsyncEnumerable<(ApiOperationPolicyName PolicyName, ApiOperationName OperationName, ApiName ApiName, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fileArtifacts = await GetFilePolicies(files, serviceDirectory, cancellationToken).ToListAsync(cancellationToken);
        var configurationArtifacts = GetConfigurationPolicies(configurationJson);
        var artifacts = fileArtifacts.LeftJoin(configurationArtifacts,
                                               keySelector: artifact => (artifact.PolicyName, artifact.OperationName, artifact.ApiName),
                                               bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.PolicyName, fileArtifact.OperationName, fileArtifact.ApiName, fileArtifact.Json.Merge(configurationArtifact.Json)));

        foreach (var artifact in artifacts)
        {
            yield return artifact;
        }
    }

    private static IAsyncEnumerable<(ApiOperationPolicyName PolicyName, ApiOperationName OperationName, ApiName ApiName, JsonObject Json)> GetFilePolicies(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
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
                    return (GetPolicyName(file), GetOperationName(file), GetApiName(file), policyJson);
                });
    }

    private static IEnumerable<(ApiOperationPolicyName PolicyName, ApiOperationName OperationName, ApiName ApiName, JsonObject Json)> GetConfigurationPolicies(JsonObject configurationJson)
    {
        return GetConfigurationApis(configurationJson)
                .SelectMany(api => GetConfigurationOperations(api.ApiName, api.Json))
                .SelectMany(operation => GetConfigurationPolicies(operation.OperationName, operation.ApiName, operation.Json));
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

    private static IEnumerable<(ApiOperationName OperationName, ApiName ApiName, JsonObject Json)> GetConfigurationOperations(ApiName apiName, JsonObject configurationApiJson)
    {
        return configurationApiJson.TryGetJsonArrayProperty("operations")
                                   .IfNullEmpty()
                                   .Choose(node => node as JsonObject)
                                   .Choose(operationJsonObject =>
                                    {
                                        var name = operationJsonObject.TryGetStringProperty("name");
                                        return name is null
                                                ? null as (ApiOperationName, ApiName, JsonObject)?
                                                : (new ApiOperationName(name), apiName, operationJsonObject);
                                    });
    }

    private static IEnumerable<(ApiOperationPolicyName PolicyName, ApiOperationName OperationName, ApiName ApiName, JsonObject Json)> GetConfigurationPolicies(ApiOperationName operationName, ApiName apiName, JsonObject configurationOperationJson)
    {
        return configurationOperationJson.TryGetJsonArrayProperty("policies")
                                         .IfNullEmpty()
                                         .Choose(node => node as JsonObject)
                                         .Choose(policyJsonObject =>
                                         {
                                             var name = policyJsonObject.TryGetStringProperty("name");
                                             return name is null
                                                     ? null as (ApiOperationPolicyName, ApiOperationName, ApiName, JsonObject)?
                                                     : (new ApiOperationPolicyName(name), operationName, apiName, policyJsonObject);
                                         });
    }

    private static async ValueTask PutPolicy(ApiOperationPolicyName policyName, ApiOperationName operationName, ApiName apiName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy {policyName} for operation {operationName} in api {apiName}...", policyName, operationName, apiName);

        var policyUri = GetApiOperationPolicyUri(policyName, operationName, apiName, serviceUri);
        await putRestResource(policyUri.Uri, json, cancellationToken);
    }
}