using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


namespace common;
public sealed record ApiVersionSetId : NonEmptyString
{
    private ApiVersionSetId(string value) : base(value)
    {
    }

    public static ApiVersionSetId From(string value) => new(value);
}

public sealed record ApiVersionSetDirectory : DirectoryRecord
{
    public ApisDirectory ApisDirectory { get; }
    public ApiDisplayName ApiDisplayName { get; }

    private ApiVersionSetDirectory(ApisDirectory apisDirectory, ApiDisplayName apiDisplayName) : base(apisDirectory.Path.Append(apiDisplayName))
    {
        ApisDirectory = apisDirectory;
        ApiDisplayName = apiDisplayName;
    }

    public static ApiVersionSetDirectory From(ApisDirectory apisDirectory, ApiDisplayName apiDisplayName) => new(apisDirectory, apiDisplayName);

    public static ApiVersionSetDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        // apis/<api-name>
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


public sealed record ApiVersionSetInformationFile : FileRecord
{
    private static readonly string name = "apiVersionSet.json";

    public ApiVersionSetDirectory VersionSetDirectory { get; }

    private ApiVersionSetInformationFile(ApiVersionSetDirectory versionSetDirectory) : base(versionSetDirectory.Path.Append(name))
    {
        VersionSetDirectory = versionSetDirectory;
    }

    public static ApiVersionSetInformationFile From(ApiVersionSetDirectory versionSetDirectory) => new(versionSetDirectory);

    public static ApiVersionSetInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var apiDirectory = ApiVersionSetDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiDirectory is null ? null : new(apiDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class ApiVersionSet
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiVersionSetId versionSetId) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("apiVersionSets")
               .AppendPath(versionSetId);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("apiVersionSets");

    public static ApiVersionSetId GetIdFromFile(ApiVersionSetInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var api = Deserialize(jsonObject);

        return ApiVersionSetId.From(api.Name);
    }
    public static Models.ApiVersionSet Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.ApiVersionSet>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.ApiVersionSet api) =>
        JsonSerializer.SerializeToNode(api, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.ApiVersionSet> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiVersionSetId versionSetId, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, versionSetId);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.ApiVersionSet> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    /// <summary>
    /// Only return version sets associated with APIs in <paramref name="apiDisplayNamesToInclude"/>.
    /// </summary>
    public static async IAsyncEnumerable<Models.ApiVersionSet> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ICollection<string> apiDisplayNamesToInclude, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get version set names from apis to include
        var filteredVersionNames = await Api.List(getResources, serviceProviderUri, serviceName, apiDisplayNamesToInclude, cancellationToken)
                                            .Select(api => api.Properties.ApiVersionSetId?.Split("/").LastOrDefault())
                                            .ToListAsync(cancellationToken);

        var versionSets = List(getResources, serviceProviderUri, serviceName, cancellationToken)
                            .Where(versionSet => filteredVersionNames.Contains(versionSet.Name));

        await foreach (var versionSet in versionSets)
        {
            yield return versionSet;
        }
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.ApiVersionSet apiVersionSet, CancellationToken cancellationToken)
    {
        var versionSetId = ApiVersionSetId.From(apiVersionSet.Name);
        var uri = GetUri(serviceProviderUri, serviceName, versionSetId);
        var json = Serialize(apiVersionSet);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiVersionSetId versionSetId, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, versionSetId);
        await deleteResource(uri, cancellationToken);
    }
}