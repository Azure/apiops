using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ApiDirectory ApiDirectory { get; }

    private ApiPolicyFile(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiPolicyFile From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiDirectory is null ? null : new(apiDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class ApiPolicy
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName) =>
        Api.GetUri(serviceProviderUri, serviceName, apiName)
               .AppendPath("policies")
               .AppendPath("policy")
               .SetQueryParameter("format", "rawxml");

    public static async ValueTask<string?> TryGet(Func<Uri, CancellationToken, ValueTask<JsonObject?>> tryGetResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName);
        var json = await tryGetResource(uri, cancellationToken);

        return json?.GetJsonObjectProperty("properties")
                    .GetStringProperty("value");
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, string policyText, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["format"] = "rawxml",
                ["value"] = policyText
            }
        };

        var uri = GetUri(serviceProviderUri, serviceName, apiName);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName);
        await deleteResource(uri, cancellationToken);
    }
}