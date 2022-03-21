using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ServicePolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ServiceDirectory ServiceDirectory { get; }

    private ServicePolicyFile(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static ServicePolicyFile From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static ServicePolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo? file) =>
        name.Equals(file?.Name) && serviceDirectory.PathEquals(file.Directory)
        ? new(serviceDirectory)
        : null;
}

public static class ServicePolicy
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("policies")
               .AppendPath("policy")
               .SetQueryParameter("format", "rawxml");

    public static async ValueTask<string?> TryGet(Func<Uri, CancellationToken, ValueTask<JsonObject?>> tryGetResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName);
        var json = await tryGetResource(uri, cancellationToken);

        return json?.GetJsonObjectProperty("properties")
                    .GetStringProperty("value");
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, string policyText, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["format"] = "rawxml",
                ["value"] = policyText
            }
        };

        var uri = GetUri(serviceProviderUri, serviceName);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName);
        await deleteResource(uri, cancellationToken);
    }
}