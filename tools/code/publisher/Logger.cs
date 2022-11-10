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

internal static class Logger
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetLoggerInformationFiles(files, serviceDirectory)
                .Select(GetLoggerName)
                .ForEachParallel(async loggerName => await Delete(loggerName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<LoggerInformationFile> GetLoggerInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetLoggerInformationFile(file, serviceDirectory));
    }

    private static LoggerInformationFile? TryGetLoggerInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(LoggerInformationFile.Name) is false)
        {
            return null;
        }

        var loggerDirectory = TryGetLoggerDirectory(file.Directory, serviceDirectory);

        return loggerDirectory is null
                ? null
                : new LoggerInformationFile(loggerDirectory);
    }

    private static LoggerDirectory? TryGetLoggerDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var loggersDirectory = TryGetLoggersDirectory(directory.Parent, serviceDirectory);
        if (loggersDirectory is null)
        {
            return null;
        }

        var loggerName = new LoggerName(directory.Name);
        return new LoggerDirectory(loggerName, loggersDirectory);
    }

    private static LoggersDirectory? TryGetLoggersDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(LoggersDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new LoggersDirectory(serviceDirectory);
    }

    private static LoggerName GetLoggerName(LoggerInformationFile file)
    {
        return new(file.LoggerDirectory.GetName());
    }

    private static async ValueTask Delete(LoggerName loggerName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetLoggerUri(loggerName, serviceUri);

        logger.LogInformation("Deleting logger {loggerName}...", loggerName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static LoggerUri GetLoggerUri(LoggerName loggerName, ServiceUri serviceUri)
    {
        var loggersUri = new LoggersUri(serviceUri);
        return new LoggerUri(loggerName, loggersUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutLogger(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(LoggerName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationLoggers(configurationJson);

        return GetLoggerInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetLoggerName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(LoggerName Name, JsonObject Json)> GetConfigurationLoggers(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("loggers")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (LoggerName, JsonObject)?
                                            : (new LoggerName(name), jsonObject);
                                });
    }

    private static async ValueTask PutLogger(LoggerName loggerName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting logger {loggerName}...", loggerName);

        var uri = GetLoggerUri(loggerName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}