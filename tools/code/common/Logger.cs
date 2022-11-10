using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record LoggersUri : IArtifactUri
{
    public Uri Uri { get; }

    public LoggersUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("loggers");
    }
}

public sealed record LoggersDirectory : IArtifactDirectory
{
    public static string Name { get; } = "loggers";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public LoggersDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record LoggerName
{
    private readonly string value;

    public LoggerName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Logger name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record LoggerUri : IArtifactUri
{
    public Uri Uri { get; }

    public LoggerUri(LoggerName loggerName, LoggersUri loggersUri)
    {
        Uri = loggersUri.AppendPath(loggerName.ToString());
    }
}

public sealed record LoggerDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public LoggersDirectory LoggersDirectory { get; }

    public LoggerDirectory(LoggerName name, LoggersDirectory loggersDirectory)
    {
        Path = loggersDirectory.Path.Append(name.ToString());
        LoggersDirectory = loggersDirectory;
    }
}

public sealed record LoggerInformationFile : IArtifactFile
{
    public static string Name { get; } = "loggerInformation.json";

    public ArtifactPath Path { get; }

    public LoggerDirectory LoggerDirectory { get; }

    public LoggerInformationFile(LoggerDirectory loggerDirectory)
    {
        Path = loggerDirectory.Path.Append(Name);
        LoggerDirectory = loggerDirectory;
    }
}

public sealed record LoggerModel
{
    public required string Name { get; init; }

    public required LoggerContractProperties Properties { get; init; }

    public sealed record LoggerContractProperties
    {
        public LoggerCredentials? Credentials { get; init; }
        public string? Description { get; init; }
        public bool? IsBuffered { get; init; }
        public LoggerTypeOption? LoggerType { get; init; }
        public string? ResourceId { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("credentials", Credentials?.Serialize())
                .AddPropertyIfNotNull("description", Description)
                .AddPropertyIfNotNull("isBuffered", IsBuffered)
                .AddPropertyIfNotNull("loggerType", LoggerType?.Serialize())
                .AddPropertyIfNotNull("resourceId", ResourceId);

        public static LoggerContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                Credentials = jsonObject.TryGetJsonObjectProperty("credentials")
                                         .Map(LoggerCredentials.Deserialize),
                Description = jsonObject.TryGetStringProperty("description"),
                IsBuffered = jsonObject.TryGetBoolProperty("isBuffered"),
                LoggerType = jsonObject.TryGetProperty("loggerType")
                                             .Map(LoggerTypeOption.Deserialize),
                ResourceId = jsonObject.TryGetStringProperty("resourceId")
            };

        public sealed record LoggerCredentials
        {
            public string? Name { get; init; }
            public string? ConnectionString { get; init; }
            public string? InstrumentationKey { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("name", Name)
                    .AddPropertyIfNotNull("connectionString", ConnectionString)
                    .AddPropertyIfNotNull("instrumentationKey", InstrumentationKey);

            public static LoggerCredentials Deserialize(JsonObject jsonObject) =>
                new()
                {
                    Name = jsonObject.TryGetStringProperty("name"),
                    ConnectionString = jsonObject.TryGetStringProperty("connectionString"),
                    InstrumentationKey = jsonObject.TryGetStringProperty("instrumentationKey")
                };
        }

        public sealed record LoggerTypeOption
        {
            private readonly string value;

            private LoggerTypeOption(string value)
            {
                this.value = value;
            }

            public static LoggerTypeOption ApplicationInsights => new("applicationInsights");
            public static LoggerTypeOption AzureEventHub => new("azureEventHub");
            public static LoggerTypeOption AzureMonitor => new("azureMonitor");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static LoggerTypeOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(ApplicationInsights).Equals(value, StringComparison.OrdinalIgnoreCase) => ApplicationInsights,
                        _ when nameof(AzureEventHub).Equals(value, StringComparison.OrdinalIgnoreCase) => AzureEventHub,
                        _ when nameof(AzureMonitor).Equals(value, StringComparison.OrdinalIgnoreCase) => AzureMonitor,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(LoggerTypeOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static LoggerModel Deserialize(LoggerName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(LoggerContractProperties.Deserialize)!
        };
}