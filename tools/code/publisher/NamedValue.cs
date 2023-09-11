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

internal static class NamedValue
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetNamedValueInformationFiles(files, serviceDirectory)
                .Select(GetNamedValueName)
                .ForEachParallel(async namedValueName => await Delete(namedValueName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<NamedValueInformationFile> GetNamedValueInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetNamedValueInformationFile(file, serviceDirectory));
    }

    private static NamedValueInformationFile? TryGetNamedValueInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(NamedValueInformationFile.Name) is false)
        {
            return null;
        }

        var namedValueDirectory = TryGetNamedValueDirectory(file.Directory, serviceDirectory);

        return namedValueDirectory is null
                ? null
                : new NamedValueInformationFile(namedValueDirectory);
    }

    private static NamedValueDirectory? TryGetNamedValueDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var namedValuesDirectory = TryGetNamedValuesDirectory(directory.Parent, serviceDirectory);
        if (namedValuesDirectory is null)
        {
            return null;
        }

        var namedValueName = new NamedValueName(directory.Name);
        return new NamedValueDirectory(namedValueName, namedValuesDirectory);
    }

    private static NamedValuesDirectory? TryGetNamedValuesDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(NamedValuesDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new NamedValuesDirectory(serviceDirectory);
    }

    private static NamedValueName GetNamedValueName(NamedValueInformationFile file)
    {
        return new(file.NamedValueDirectory.GetName());
    }

    private static async ValueTask Delete(NamedValueName namedValueName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetNamedValueUri(namedValueName, serviceUri);

        logger.LogInformation("Deleting named value {namedValueName}...", namedValueName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static NamedValueUri GetNamedValueUri(NamedValueName namedValueName, ServiceUri serviceUri)
    {
        var namedValuesUri = new NamedValuesUri(serviceUri);
        return new NamedValueUri(namedValueName, namedValuesUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutNamedValue(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(NamedValueName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationNamedValues(configurationJson);

        return GetNamedValueInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetNamedValueName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(NamedValueName Name, JsonObject Json)> GetConfigurationNamedValues(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("namedValues")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (NamedValueName, JsonObject)?
                                            : (new NamedValueName(name), jsonObject);
                                });
    }

    private static async ValueTask PutNamedValue(NamedValueName namedValueName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var namedValue = NamedValueModel.Deserialize(namedValueName, json);

        switch (namedValue.Properties.Secret, namedValue.Properties.Value, namedValue.Properties.KeyVault?.SecretIdentifier)
        {
            case (true, null, null):
                logger.LogWarning("Named value {namedValueName} is secret, but no value or keyvault identifier was specified. Skipping it...", namedValueName);
                return;
            default:
                logger.LogInformation("Putting named value {namedValueName}...", namedValueName);

                var uri = GetNamedValueUri(namedValueName, serviceUri);
                await putRestResource(uri.Uri, json, cancellationToken);
                return;
        }
    }
}