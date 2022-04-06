using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record GatewayApisFile : FileRecord
{
    private static readonly string name = "apis.json";

    public GatewayDirectory GatewayDirectory { get; }

    private GatewayApisFile(GatewayDirectory gatewayDirectory) : base(gatewayDirectory.Path.Append(name))
    {
        GatewayDirectory = gatewayDirectory;
    }

    public static GatewayApisFile From(GatewayDirectory gatewayDirectory) => new(gatewayDirectory);

    public static GatewayApisFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var gatewayDirectory = GatewayDirectory.TryFrom(serviceDirectory, file.Directory);

            return gatewayDirectory is null ? null : new(gatewayDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class GatewayApi
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName, ApiName apiName) =>
        Gateway.GetUri(serviceProviderUri, serviceName, gatewayName)
               .AppendPath("apis")
               .AppendPath(apiName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName) =>
        Gateway.GetUri(serviceProviderUri, serviceName, gatewayName)
               .AppendPath("apis");

    public static ImmutableList<ApiName> ListFromFile(GatewayApisFile file) =>
        file.ReadAsJsonArray()
            .Where(node => node is not null)
            .Select(node => node!.AsObject())
            .Select(jsonObject => jsonObject.GetStringProperty("name"))
            .Select(ApiName.From)
            .ToImmutableList();

    public static IAsyncEnumerable<Models.Api> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName, gatewayName);
        return getResources(uri, cancellationToken).Select(Api.Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, gatewayName, apiName);
        await putResource(uri, new JsonObject(), cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, gatewayName, apiName);
        await deleteResource(uri, cancellationToken);
    }
}