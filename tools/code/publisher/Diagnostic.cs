using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class Diagnostic
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetDiagnosticInformationFiles(files, serviceDirectory)
                .Select(GetDiagnosticName)
                .ForEachParallel(async diagnosticName => await Delete(diagnosticName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<DiagnosticInformationFile> GetDiagnosticInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetDiagnosticInformationFile(file, serviceDirectory));
    }

    private static DiagnosticInformationFile? TryGetDiagnosticInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(DiagnosticInformationFile.Name) is false)
        {
            return null;
        }

        var diagnosticDirectory = TryGetDiagnosticDirectory(file.Directory, serviceDirectory);

        return diagnosticDirectory is null
                ? null
                : new DiagnosticInformationFile(diagnosticDirectory);
    }

    private static DiagnosticDirectory? TryGetDiagnosticDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var diagnosticsDirectory = TryGetDiagnosticsDirectory(directory.Parent, serviceDirectory);
        if (diagnosticsDirectory is null)
        {
            return null;
        }

        var diagnosticName = new DiagnosticName(directory.Name);
        return new DiagnosticDirectory(diagnosticName, diagnosticsDirectory);
    }

    private static DiagnosticsDirectory? TryGetDiagnosticsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(DiagnosticsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new DiagnosticsDirectory(serviceDirectory);
    }

    private static DiagnosticName GetDiagnosticName(DiagnosticInformationFile file)
    {
        return new(file.DiagnosticDirectory.GetName());
    }

    private static async ValueTask Delete(DiagnosticName diagnosticName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetDiagnosticUri(diagnosticName, serviceUri);

        logger.LogInformation("Deleting diagnostic {diagnosticName}...", diagnosticName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static DiagnosticUri GetDiagnosticUri(DiagnosticName diagnosticName, ServiceUri serviceUri)
    {
        var diagnosticsUri = new DiagnosticsUri(serviceUri);
        return new DiagnosticUri(diagnosticName, diagnosticsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutDiagnostic(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(DiagnosticName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationDiagnostics(configurationJson);

        return GetDiagnosticInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetDiagnosticName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(DiagnosticName Name, JsonObject Json)> GetConfigurationDiagnostics(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("diagnostics")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (DiagnosticName, JsonObject)?
                                            : (new DiagnosticName(name), jsonObject);
                                });
    }

    private static async ValueTask PutDiagnostic(DiagnosticName diagnosticName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting diagnostic {diagnosticName}...", diagnosticName);

        var uri = GetDiagnosticUri(diagnosticName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}