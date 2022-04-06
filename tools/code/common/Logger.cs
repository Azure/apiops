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

public sealed record LoggerName : NonEmptyString
{
    private LoggerName(string value) : base(value)
    {
    }

    public static LoggerName From(string value) => new(value);
}

public sealed record LoggersDirectory : DirectoryRecord
{
    private static readonly string name = "loggers";

    public ServiceDirectory ServiceDirectory { get; }

    private LoggersDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static LoggersDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static LoggersDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record LoggerDirectory : DirectoryRecord
{
    public LoggersDirectory LoggersDirectory { get; }
    public LoggerName LoggerName { get; }

    private LoggerDirectory(LoggersDirectory loggersDirectory, LoggerName loggerName) : base(loggersDirectory.Path.Append(loggerName))
    {
        LoggersDirectory = loggersDirectory;
        LoggerName = loggerName;
    }

    public static LoggerDirectory From(LoggersDirectory loggersDirectory, LoggerName loggerName) => new(loggersDirectory, loggerName);

    public static LoggerDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var loggersDirectory = LoggersDirectory.TryFrom(serviceDirectory, parentDirectory);

            return loggersDirectory is null ? null : From(loggersDirectory, LoggerName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record LoggerInformationFile : FileRecord
{
    private static readonly string name = "loggerInformation.json";

    public LoggerDirectory LoggerDirectory { get; }

    private LoggerInformationFile(LoggerDirectory loggerDirectory) : base(loggerDirectory.Path.Append(name))
    {
        LoggerDirectory = loggerDirectory;
    }

    public static LoggerInformationFile From(LoggerDirectory loggerDirectory) => new(loggerDirectory);

    public static LoggerInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var loggerDirectory = LoggerDirectory.TryFrom(serviceDirectory, file.Directory);

            return loggerDirectory is null ? null : new(loggerDirectory);
        }
        else
        {
            return null;
        }
    }
}

public static class Logger
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, LoggerName loggerName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("loggers")
               .AppendPath(loggerName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("loggers");

    public static LoggerName GetNameFromFile(LoggerInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var logger = Deserialize(jsonObject);

        return LoggerName.From(logger.Name);
    }

    public static Models.Logger Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Logger>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Logger logger) =>
        JsonSerializer.SerializeToNode(logger, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Logger> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, LoggerName loggerName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, loggerName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.Logger> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.Logger logger, CancellationToken cancellationToken)
    {
        var name = LoggerName.From(logger.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(logger);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, LoggerName loggerName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, loggerName);
        await deleteResource(uri, cancellationToken);
    }
}
