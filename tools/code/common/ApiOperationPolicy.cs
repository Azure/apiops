using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiOperationPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ApiOperationDirectory ApiOperationDirectory { get; }

    private ApiOperationPolicyFile(ApiOperationDirectory apiOperationDirectory) : base(apiOperationDirectory.Path.Append(name))
    {
        ApiOperationDirectory = apiOperationDirectory;
    }

    public static ApiOperationPolicyFile From(ApiOperationDirectory apiOperationDirectory) => new(apiOperationDirectory);

    public static ApiOperationPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var apiOperationDirectory = ApiOperationDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiOperationDirectory is null ? null : new(apiOperationDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class ApiOperationPolicy
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName) =>
        ApiOperation.GetUri(serviceProviderUri, serviceName, apiName, apiOperationName)
                    .AppendPath("policies")
                    .AppendPath("policy")
                    .SetQueryParameter("format", "rawxml");

    public static async ValueTask<string?> TryGet(Func<Uri, CancellationToken, ValueTask<JsonObject?>> tryGetResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiOperationName);
        var json = await tryGetResource(uri, cancellationToken);

        return json?.GetJsonObjectProperty("properties")
                    .GetStringProperty("value");
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName, string policyText, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["format"] = "rawxml",
                ["value"] = policyText
            }
        };

        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiOperationName);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiOperationName);
        await deleteResource(uri, cancellationToken);
    }
}