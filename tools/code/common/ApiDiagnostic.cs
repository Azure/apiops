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

public sealed record ApiDiagnosticName : NonEmptyString
{
    private ApiDiagnosticName(string value) : base(value)
    {
    }

    public static ApiDiagnosticName From(string value) => new(value);
}

public sealed record ApiDiagnosticsDirectory : DirectoryRecord
{
    private static readonly string name = "diagnostics";

    public ApiDirectory ApiDirectory { get; }

    private ApiDiagnosticsDirectory(ApiDirectory apiDirectory) : base(apiDirectory.Path.Append(name))
    {
        ApiDirectory = apiDirectory;
    }

    public static ApiDiagnosticsDirectory From(ApiDirectory apiDirectory) => new(apiDirectory);

    public static ApiDiagnosticsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
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

public sealed record ApiDiagnosticDirectory : DirectoryRecord
{
    public ApiDiagnosticsDirectory ApiDiagnosticsDirectory { get; }
    public ApiDiagnosticName ApiDiagnosticName { get; }

    private ApiDiagnosticDirectory(ApiDiagnosticsDirectory apiDiagnosticsDirectory, ApiDiagnosticName apiDiagnosticName) : base(apiDiagnosticsDirectory.Path.Append(apiDiagnosticName))
    {
        ApiDiagnosticsDirectory = apiDiagnosticsDirectory;
        ApiDiagnosticName = apiDiagnosticName;
    }

    public static ApiDiagnosticDirectory From(ApiDiagnosticsDirectory apiDiagnosticsDirectory, ApiDiagnosticName apiDiagnosticName) => new(apiDiagnosticsDirectory, apiDiagnosticName);

    public static ApiDiagnosticDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var apiDiagnosticsDirectory = ApiDiagnosticsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return apiDiagnosticsDirectory is null ? null : From(apiDiagnosticsDirectory, ApiDiagnosticName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record ApiDiagnosticInformationFile : FileRecord
{
    private static readonly string name = "diagnosticInformation.json";

    public ApiDiagnosticDirectory ApiDiagnosticDirectory { get; }

    private ApiDiagnosticInformationFile(ApiDiagnosticDirectory apiDiagnosticDirectory) : base(apiDiagnosticDirectory.Path.Append(name))
    {
        ApiDiagnosticDirectory = apiDiagnosticDirectory;
    }

    public static ApiDiagnosticInformationFile From(ApiDiagnosticDirectory apiDiagnosticDirectory) => new(apiDiagnosticDirectory);

    public static ApiDiagnosticInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo? file)
    {
        if (name.Equals(file?.Name))
        {
            var apiDiagnosticDirectory = ApiDiagnosticDirectory.TryFrom(serviceDirectory, file.Directory);

            return apiDiagnosticDirectory is null ? null : new(apiDiagnosticDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class ApiDiagnostic
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiDiagnosticName apiDiagnosticName) =>
        Api.GetUri(serviceProviderUri, serviceName, apiName)
           .AppendPath("diagnostics")
           .AppendPath(apiDiagnosticName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName) =>
        Api.GetUri(serviceProviderUri, serviceName, apiName)
           .AppendPath("diagnostics");

    public static ApiDiagnosticName GetNameFromFile(ApiDiagnosticInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var apiDiagnostic = Deserialize(jsonObject);

        return ApiDiagnosticName.From(apiDiagnostic.Name);
    }

    public static Models.ApiDiagnostic Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.ApiDiagnostic>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.ApiDiagnostic apiDiagnostic) =>
        JsonSerializer.SerializeToNode(apiDiagnostic, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.ApiDiagnostic> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiDiagnosticName apiDiagnosticName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiDiagnosticName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.ApiDiagnostic> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName, apiName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, Models.ApiDiagnostic apiDiagnostic, CancellationToken cancellationToken)
    {
        var name = ApiDiagnosticName.From(apiDiagnostic.Name);
        var uri = GetUri(serviceProviderUri, serviceName, apiName, name);
        var json = Serialize(apiDiagnostic);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiDiagnosticName apiDiagnosticName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, apiDiagnosticName);
        await deleteResource(uri, cancellationToken);
    }
}