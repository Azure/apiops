using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ServiceName : NonEmptyString
{
    private ServiceName(string value) : base(value)
    {
    }

    public static ServiceName From(string value) => new(value);
}

public sealed record ServiceProviderUri : UriRecord
{
    private ServiceProviderUri(Uri value) : base(value)
    {
    }

    public static ServiceProviderUri From(string resourceManagerEndpoint, string subscriptionId, string resourceGroupName) =>
        From(new Uri(resourceManagerEndpoint), subscriptionId, resourceGroupName);

    public static ServiceProviderUri From(Uri resourceManagerEndpoint, string subscriptionId, string resourceGroupName)
    {
        var uri = resourceManagerEndpoint.AppendPath("subscriptions")
                                         .AppendPath(subscriptionId)
                                         .AppendPath("resourceGroups")
                                         .AppendPath(resourceGroupName)
                                         .AppendPath("providers/Microsoft.ApiManagement/service")
                                         .SetQueryParameter("api-version", "2021-08-01");

        return new(uri);
    }
}

public sealed record ServiceDirectory : DirectoryRecord
{
    private ServiceDirectory(RecordPath path) : base(path)
    {
    }

    public static ServiceDirectory From(string path) => new(RecordPath.From(path));
}

public sealed record ServiceInformationFile : FileRecord
{
    private static readonly string name = "serviceInformation.json";

    public ServiceDirectory ServiceDirectory { get; }

    private ServiceInformationFile(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static ServiceInformationFile From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static ServiceInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo? file) =>
        name.Equals(file?.Name) && serviceDirectory.PathEquals(file?.Directory)
        ? new(serviceDirectory)
        : null;
}

public static class Service
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        serviceProviderUri.ToUri().AppendPath(serviceName);

    public static ServiceName GetNameFromFile(ServiceInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var service = Deserialize(jsonObject);

        return ServiceName.From(service.Name);
    }

    public static Models.Service Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Service>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Service service) =>
        JsonSerializer.SerializeToNode(service, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Service> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, Models.Service service, CancellationToken cancellationToken)
    {
        var name = ServiceName.From(service.Name);
        var uri = GetUri(serviceProviderUri, name);
        var json = Serialize(service);
        await putResource(uri, json, cancellationToken);
    }
}