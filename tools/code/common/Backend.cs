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

public sealed record BackendName : NonEmptyString
{
    private BackendName(string value) : base(value)
    {
    }

    public static BackendName From(string value) => new(value);
}

public sealed record BackendsDirectory : DirectoryRecord
{
    private static readonly string name = "backends";

    public ServiceDirectory ServiceDirectory { get; }

    private BackendsDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static BackendsDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static BackendsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record BackendDirectory : DirectoryRecord
{
    public BackendsDirectory BackendsDirectory { get; }
    public BackendName BackendName { get; }

    private BackendDirectory(BackendsDirectory backendsDirectory, BackendName backendName) : base(backendsDirectory.Path.Append(backendName))
    {
        BackendsDirectory = backendsDirectory;
        BackendName = backendName;
    }

    public static BackendDirectory From(BackendsDirectory backendsDirectory, BackendName backendName) => new(backendsDirectory, backendName);

    public static BackendDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var backendsDirectory = BackendsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return backendsDirectory is null ? null : From(backendsDirectory, BackendName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record BackendInformationFile : FileRecord
{
    private static readonly string name = "backendInformation.json";

    public BackendDirectory BackendDirectory { get; }

    private BackendInformationFile(BackendDirectory backendDirectory) : base(backendDirectory.Path.Append(name))
    {
        BackendDirectory = backendDirectory;
    }

    public static BackendInformationFile From(BackendDirectory backendDirectory) => new(backendDirectory);

    public static BackendInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var backendDirectory = BackendDirectory.TryFrom(serviceDirectory, file.Directory);

            return backendDirectory is null ? null : new(backendDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class Backend
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, BackendName backendName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("backends")
               .AppendPath(backendName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("backends");

    public static BackendName GetNameFromFile(BackendInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var backend = Deserialize(jsonObject);

        return BackendName.From(backend.Name);
    }

    public static Models.Backend Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Backend>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Backend backend) =>
        JsonSerializer.SerializeToNode(backend, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Backend> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, BackendName backendName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, backendName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.Backend> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.Backend backend, CancellationToken cancellationToken)
    {
        var name = BackendName.From(backend.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(backend);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, BackendName backendName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, backendName);
        await deleteResource(uri, cancellationToken);
    }
}
