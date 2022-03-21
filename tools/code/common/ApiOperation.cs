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

public sealed record ApiOperationName : NonEmptyString
{
    private ApiOperationName(string value) : base(value)
    {
    }

    public static ApiOperationName From(string value) => new(value);
}

public sealed record ApiOperationDisplayName : NonEmptyString
{
    private ApiOperationDisplayName(string value) : base(value)
    {
    }

    public static ApiOperationDisplayName From(string value) => new(value);
}

public sealed record ApiOperationsDirectory : DirectoryRecord
{
    private static readonly string name = "operations";

    public ApiDirectory ApiDirectory { get; }

    private ApiOperationsDirectory(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiOperationsDirectory From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiOperationsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        if (name.Equals(directory?.Name))
        {
            var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, directory.Parent);

            return apiDirectory is null ? null : new(apiDirectory);
        }
        else
        {
            return null;
        }
    }
}

public sealed record ApiOperationDirectory : DirectoryRecord
{
    public ApiOperationsDirectory ApiOperationsDirectory { get; }
    public ApiOperationDisplayName ApiOperationDisplayName { get; }

    private ApiOperationDirectory(ApiOperationsDirectory apiOperationsDirectory, ApiOperationDisplayName apiOperationDisplayName) : base(apiOperationsDirectory.Path.Append(apiOperationDisplayName))
    {
        ApiOperationsDirectory = apiOperationsDirectory;
        ApiOperationDisplayName = apiOperationDisplayName;
    }

    public static ApiOperationDirectory From(ApiOperationsDirectory apiOperationsDirectory, ApiOperationDisplayName apiOperationDisplayName) => new(apiOperationsDirectory, apiOperationDisplayName);

    public static ApiOperationDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var apiOperationsDirectory = ApiOperationsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return apiOperationsDirectory is null ? null : From(apiOperationsDirectory, ApiOperationDisplayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public static class ApiOperation
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName) =>
        Api.GetUri(serviceProviderUri, serviceName, apiName)
           .AppendPath("operations")
           .AppendPath(apiOperationName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName) =>
        Api.GetUri(serviceProviderUri, serviceName, apiName)
           .AppendPath("operations");

    public static Models.ApiOperation Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.ApiOperation>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.ApiOperation apiOperation) =>
        JsonSerializer.SerializeToNode(apiOperation, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.ApiOperation> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiOperationName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.ApiOperation> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName, apiName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, Models.ApiOperation apiOperation, CancellationToken cancellationToken)
    {
        var name = ApiOperationName.From(apiOperation.Name);
        var uri = GetUri(serviceProviderUri, serviceName, apiName, name);
        var json = Serialize(apiOperation);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiOperationName apiOperationName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiOperationName);
        await deleteResource(uri, cancellationToken);
    }
}