using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record GatewayName : NonEmptyString
{
    private GatewayName(string value) : base(value)
    {
    }

    public static GatewayName From(string value) => new(value);
}

public sealed record GatewaysDirectory : DirectoryRecord
{
    private static readonly string name = "gateways";

    public ServiceDirectory ServiceDirectory { get; }

    private GatewaysDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static GatewaysDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static GatewaysDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record GatewayDirectory : DirectoryRecord
{
    public GatewaysDirectory GatewaysDirectory { get; }
    public GatewayName GatewayName { get; }

    private GatewayDirectory(GatewaysDirectory gatewaysDirectory, GatewayName gatewayName) : base(gatewaysDirectory.Path.Append(gatewayName))
    {
        GatewaysDirectory = gatewaysDirectory;
        GatewayName = gatewayName;
    }

    public static GatewayDirectory From(GatewaysDirectory gatewaysDirectory, GatewayName gatewayName) => new(gatewaysDirectory, gatewayName);

    public static GatewayDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var gatewaysDirectory = GatewaysDirectory.TryFrom(serviceDirectory, parentDirectory);

            return gatewaysDirectory is null ? null : From(gatewaysDirectory, GatewayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record GatewayInformationFile : FileRecord
{
    private static readonly string name = "gatewayInformation.json";

    public GatewayDirectory GatewayDirectory { get; }

    private GatewayInformationFile(GatewayDirectory gatewayDirectory) : base(gatewayDirectory.Path.Append(name))
    {
        GatewayDirectory = gatewayDirectory;
    }

    public static GatewayInformationFile From(GatewayDirectory gatewayDirectory) => new(gatewayDirectory);

    public static GatewayInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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

public static class Gateway
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("gateways")
               .AppendPath(gatewayName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("gateways");

    public static GatewayName GetNameFromFile(GatewayInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var gateway = Deserialize(jsonObject);

        return GatewayName.From(gateway.Name);
    }

    public static Models.Gateway Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Gateway>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Gateway gateway) =>
        JsonSerializer.SerializeToNode(gateway, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Gateway> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, gatewayName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.Gateway> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.Gateway gateway, CancellationToken cancellationToken)
    {
        var name = GatewayName.From(gateway.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(gateway);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, gatewayName);
        await deleteResource(uri, cancellationToken);
    }
}
