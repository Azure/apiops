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

public sealed record NamedValueName : NonEmptyString
{
    private NamedValueName(string value) : base(value)
    {
    }

    public static NamedValueName From(string value) => new(value);
}

public sealed record NamedValueDisplayName : NonEmptyString
{
    private NamedValueDisplayName(string value) : base(value)
    {
    }

    public static NamedValueDisplayName From(string value) => new(value);
}

public sealed record NamedValuesDirectory : DirectoryRecord
{
    private static readonly string name = "named values";

    public ServiceDirectory ServiceDirectory { get; }

    private NamedValuesDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static NamedValuesDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static NamedValuesDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record NamedValueDirectory : DirectoryRecord
{
    public NamedValuesDirectory NamedValuesDirectory { get; }
    public NamedValueDisplayName NamedValueDisplayName { get; }

    private NamedValueDirectory(NamedValuesDirectory namedValuesDirectory, NamedValueDisplayName namedValueDisplayName) : base(namedValuesDirectory.Path.Append(namedValueDisplayName))
    {
        NamedValuesDirectory = namedValuesDirectory;
        NamedValueDisplayName = namedValueDisplayName;
    }

    public static NamedValueDirectory From(NamedValuesDirectory namedValuesDirectory, NamedValueDisplayName namedValueDisplayName) => new(namedValuesDirectory, namedValueDisplayName);

    public static NamedValueDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var namedValuesDirectory = NamedValuesDirectory.TryFrom(serviceDirectory, parentDirectory);

            return namedValuesDirectory is null ? null : From(namedValuesDirectory, NamedValueDisplayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record NamedValueInformationFile : FileRecord
{
    private static readonly string name = "namedValueInformation.json";

    public NamedValueDirectory NamedValueDirectory { get; }

    private NamedValueInformationFile(NamedValueDirectory namedValueDirectory) : base(namedValueDirectory.Path.Append(name))
    {
        NamedValueDirectory = namedValueDirectory;
    }

    public static NamedValueInformationFile From(NamedValueDirectory namedValueDirectory) => new(namedValueDirectory);

    public static NamedValueInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var namedValueDirectory = NamedValueDirectory.TryFrom(serviceDirectory, file.Directory);

            return namedValueDirectory is null ? null : new(namedValueDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class NamedValue
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, NamedValueName namedValueName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("namedValues")
               .AppendPath(namedValueName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("namedValues");

    public static NamedValueName GetNameFromFile(NamedValueInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var namedValue = Deserialize(jsonObject);

        return NamedValueName.From(namedValue.Name);
    }

    public static Models.NamedValue Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.NamedValue>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.NamedValue namedValue) =>
        JsonSerializer.SerializeToNode(namedValue, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.NamedValue> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, NamedValueName namedValueName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, namedValueName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.NamedValue> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.NamedValue namedValue, CancellationToken cancellationToken)
    {
        var name = NamedValueName.From(namedValue.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(namedValue);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, NamedValueName namedValueName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, namedValueName);
        await deleteResource(uri, cancellationToken);
    }
}