using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ApiDiagnostic
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetDiagnosticInformationFiles(files, serviceDirectory)
                .Select(file => (DiagnosticName: GetDiagnosticName(file), ApiName: GetApiName(file)))
                .ForEachParallel(async diagnostic => await Delete(diagnostic.DiagnosticName, diagnostic.ApiName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ApiDiagnosticInformationFile> GetDiagnosticInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetApiDiagnosticInformationFile(file, serviceDirectory));
    }

    private static ApiDiagnosticInformationFile? TryGetApiDiagnosticInformationFile(FileInfo file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ApiDiagnosticInformationFile.Name) is false)
        {
            return null;
        }

        var diagnosticDirectory = TryGetApiDiagnosticDirectory(file.Directory, serviceDirectory);
        return diagnosticDirectory is null
                ? null
                : new ApiDiagnosticInformationFile(diagnosticDirectory);
    }

    private static ApiDiagnosticDirectory? TryGetApiDiagnosticDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var apiDiagnosticsDirectory = TryGetApiDiagnosticsDirectory(directory.Parent, serviceDirectory);
        if (apiDiagnosticsDirectory is null)
        {
            return null;
        }

        var apiDiagnosticName = new ApiDiagnosticName(directory.Name);
        return new ApiDiagnosticDirectory(apiDiagnosticName, apiDiagnosticsDirectory);
    }

    private static ApiDiagnosticsDirectory? TryGetApiDiagnosticsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null || directory.Name.Equals(ApiDiagnosticsDirectory.Name) is false)
        {
            return null;
        }

        var apiDirectory = Api.TryGetApiDirectory(directory?.Parent, serviceDirectory);

        return apiDirectory is null
                ? null
                : new ApiDiagnosticsDirectory(apiDirectory);
    }

    private static ApiDiagnosticName GetDiagnosticName(ApiDiagnosticInformationFile file)
    {
        return new(file.ApiDiagnosticDirectory.GetName());
    }

    private static ApiName GetApiName(ApiDiagnosticInformationFile file)
    {
        return new(file.ApiDiagnosticDirectory.ApiDiagnosticsDirectory.ApiDirectory.GetName());
    }

    private static async ValueTask Delete(ApiDiagnosticName diagnosticName, ApiName apiName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting diagnostic {diagnosticName} in API {apiName}...", diagnosticName, apiName);

        var uri = GetDiagnosticUri(diagnosticName, apiName, serviceUri);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static ApiDiagnosticUri GetDiagnosticUri(ApiDiagnosticName diagnosticName, ApiName apiName, ServiceUri serviceUri)
    {
        var apiUri = Api.GetApiUri(apiName, serviceUri);
        var diagnosticsUri = new ApiDiagnosticsUri(apiUri);
        return new ApiDiagnosticUri(diagnosticName, diagnosticsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await Put(artifact.DiagnosticName, artifact.ApiName, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ApiDiagnosticName DiagnosticName, ApiName ApiName, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationDiagnostics(configurationJson);

        return GetDiagnosticInformationFiles(files, serviceDirectory)
                .Select(file => (DiagnosticName: GetDiagnosticName(file), ApiName: GetApiName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => (artifact.DiagnosticName, artifact.ApiName),
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.DiagnosticName, fileArtifact.ApiName, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(ApiDiagnosticName DiagnosticName, ApiName ApiName, JsonObject Json)> GetConfigurationDiagnostics(JsonObject configurationJson)
    {
        return GetConfigurationApis(configurationJson)
                .SelectMany(api => GetConfigurationDiagnostics(api.ApiName, api.Json));
    }

    private static IEnumerable<(ApiName ApiName, JsonObject Json)> GetConfigurationApis(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("apis")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(apiJsonObject =>
                                {
                                    var name = apiJsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (ApiName, JsonObject)?
                                            : (new ApiName(name), apiJsonObject);
                                });
    }

    private static IEnumerable<(ApiDiagnosticName DiagnosticName, ApiName ApiName, JsonObject Json)> GetConfigurationDiagnostics(ApiName apiName, JsonObject configurationApiJson)
    {
        return configurationApiJson.TryGetJsonArrayProperty("diagnostics")
                                   .IfNullEmpty()
                                   .Choose(node => node as JsonObject)
                                   .Choose(jsonObject =>
                                   {
                                       var name = jsonObject.TryGetStringProperty("name");
                                       return name is null
                                               ? null as (ApiDiagnosticName, ApiName, JsonObject)?
                                               : (new ApiDiagnosticName(name), apiName, jsonObject);
                                   });
    }

    private static async ValueTask Put(ApiDiagnosticName diagnosticName, ApiName apiName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("PUtting diagnostic {diagnosticName} in API {apiName}...", diagnosticName, apiName);

        var uri = GetDiagnosticUri(diagnosticName, apiName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}