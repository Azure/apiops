using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record LoggerName : NonEmptyString
{
    private LoggerName(string value) : base(value)
    {
    }

    public static LoggerName From(string value) => new(value);

    public static LoggerName From(LoggerInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var logger = Logger.FromJsonObject(jsonObject);

        return new LoggerName(logger.Name);
    }
}

public sealed record LoggerUri : UriRecord
{
    public LoggerUri(Uri value) : base(value)
    {
    }

    public static LoggerUri From(ServiceUri serviceUri, LoggerName loggerName) =>
        new(UriExtensions.AppendPath(serviceUri, "loggers").AppendPath(loggerName));
}

public sealed record LoggersDirectory : DirectoryRecord
{
    private static readonly string name = "loggers";

    private LoggersDirectory(RecordPath path) : base(path)
    {
    }

    public static LoggersDirectory From(ServiceDirectory serviceDirectory) =>
        new(serviceDirectory.Path.Append(name));

    public static LoggersDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.Path.PathEquals(directory.Parent?.FullName)
        ? new(RecordPath.From(directory.FullName))
        : null;
}

public sealed record LoggerInformationFile : FileRecord
{
    private static readonly string name = "loggerInformation.json";

    public LoggerInformationFile(RecordPath path) : base(path)
    {
    }

    public static LoggerInformationFile From(LoggersDirectory loggersDirectory, LoggerName loggerName)
        => new(loggersDirectory.Path.Append(loggerName)
                                     .Append(name));

    public static LoggerInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file) =>
        name.Equals(file.Name) && LoggersDirectory.TryFrom(serviceDirectory, file.Directory?.Parent) is not null
        ? new(RecordPath.From(file.FullName))
        : null;
}

public sealed record Logger([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Logger.LoggerContractProperties Properties)
{
    public record LoggerContractProperties
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }
        [JsonPropertyName("isBuffered")]
        public bool? IsBuffered { get; init; }
        [JsonPropertyName("loggerType")]
        public string? LoggerType { get; init; }
        [JsonPropertyName("resourceId")]
        public string? ResourceId { get; init; }
        [JsonPropertyName("credentials")]
        public Credentials? Credentials { get; init; }
    }

    public record Credentials
    {
        [JsonPropertyName("instrumentationKey")]
        public string? InstrumentationKey { get; init; }
        [JsonPropertyName("name")]
        public string? Name { get; init; }
        [JsonPropertyName("connectionString")]
        public string? ConnectionString { get; init; }
    }

    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static Logger FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Logger>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByServiceUri(ServiceUri serviceUri) => UriExtensions.AppendPath(serviceUri, "loggers");
}
