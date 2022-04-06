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

public sealed record DiagnosticName : NonEmptyString
{
    private DiagnosticName(string value) : base(value)
    {
    }

    public static DiagnosticName From(string value) => new(value);
}

public sealed record DiagnosticsDirectory : DirectoryRecord
{
    private static readonly string name = "diagnostics";

    public ServiceDirectory ServiceDirectory { get; }

    private DiagnosticsDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static DiagnosticsDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static DiagnosticsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record DiagnosticDirectory : DirectoryRecord
{
    public DiagnosticsDirectory DiagnosticsDirectory { get; }
    public DiagnosticName DiagnosticName { get; }

    private DiagnosticDirectory(DiagnosticsDirectory diagnosticsDirectory, DiagnosticName diagnosticName) : base(diagnosticsDirectory.Path.Append(diagnosticName))
    {
        DiagnosticsDirectory = diagnosticsDirectory;
        DiagnosticName = diagnosticName;
    }

    public static DiagnosticDirectory From(DiagnosticsDirectory diagnosticsDirectory, DiagnosticName diagnosticName) => new(diagnosticsDirectory, diagnosticName);

    public static DiagnosticDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var diagnosticsDirectory = DiagnosticsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return diagnosticsDirectory is null ? null : From(diagnosticsDirectory, DiagnosticName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record DiagnosticInformationFile : FileRecord
{
    private static readonly string name = "diagnosticInformation.json";

    public DiagnosticDirectory DiagnosticDirectory { get; }

    private DiagnosticInformationFile(DiagnosticDirectory diagnosticDirectory) : base(diagnosticDirectory.Path.Append(name))
    {
        DiagnosticDirectory = diagnosticDirectory;
    }

    public static DiagnosticInformationFile From(DiagnosticDirectory diagnosticDirectory) => new(diagnosticDirectory);

    public static DiagnosticInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var diagnosticDirectory = DiagnosticDirectory.TryFrom(serviceDirectory, file.Directory);

            return diagnosticDirectory is null ? null : new(diagnosticDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class Diagnostic
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, DiagnosticName diagnosticName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("diagnostics")
               .AppendPath(diagnosticName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("diagnostics");

    public static DiagnosticName GetNameFromFile(DiagnosticInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var diagnostic = Deserialize(jsonObject);

        return DiagnosticName.From(diagnostic.Name);
    }

    public static Models.Diagnostic Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Diagnostic>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Diagnostic diagnostic) =>
        JsonSerializer.SerializeToNode(diagnostic, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Diagnostic> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, DiagnosticName diagnosticName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, diagnosticName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.Diagnostic> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var name = DiagnosticName.From(diagnostic.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(diagnostic);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, DiagnosticName diagnosticName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, diagnosticName);
        await deleteResource(uri, cancellationToken);
    }
}
