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

public sealed record ApiName : NonEmptyString
{
    private ApiName(string value) : base(value)
    {
    }

    public static ApiName From(string value) => new(value);
}

public sealed record ApiDisplayName : NonEmptyString
{
    private ApiDisplayName(string value) : base(value)
    {
    }

    public static ApiDisplayName From(string value) => new(value);
}

public sealed record ApisDirectory : DirectoryRecord
{
    private static readonly string name = "apis";

    public ServiceDirectory ServiceDirectory { get; }

    private ApisDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static ApisDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static ApisDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record ApiDirectory : DirectoryRecord
{
    public ApisDirectory ApisDirectory { get; }
    public ApiDisplayName ApiDisplayName { get; }

    private ApiDirectory(ApisDirectory apisDirectory, ApiDisplayName apiDisplayName) : base(apisDirectory.Path.Append(apiDisplayName))
    {
        ApisDirectory = apisDirectory;
        ApiDisplayName = apiDisplayName;
    }

    public static ApiDirectory From(ApisDirectory apisDirectory, ApiDisplayName apiDisplayName) => new(apisDirectory, apiDisplayName);

    public static ApiDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var apisDirectory = ApisDirectory.TryFrom(serviceDirectory, parentDirectory);

            return apisDirectory is null ? null : From(apisDirectory, ApiDisplayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record ApiInformationFile : FileRecord
{
    private static readonly string name = "apiInformation.json";

    public ApiDirectory ApiDirectory { get; }

    private ApiInformationFile(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiInformationFile From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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

public static class Api
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("apis")
               .AppendPath(apiName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("apis");

    public static ApiName GetNameFromFile(ApiInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var api = Deserialize(jsonObject);

        return ApiName.From(api.Name);
    }

    public static Models.Api Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Api>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Api api) =>
        JsonSerializer.SerializeToNode(api, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Api> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.Api> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.Api api, CancellationToken cancellationToken)
    {
        var name = ApiName.From(api.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(api);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName);
        await deleteResource(uri, cancellationToken);
    }
}