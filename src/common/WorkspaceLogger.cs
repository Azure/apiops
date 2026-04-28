using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceLoggerResource : IResourceWithInformationFile, IChildResource
{
    private WorkspaceLoggerResource() { }

    public string FileName { get; } = "loggerInformation.json";

    public string CollectionDirectoryName { get; } = "loggers";

    public string SingularName { get; } = "logger";

    public string PluralName { get; } = "loggers";

    public string CollectionUriPath { get; } = "loggers";

    public Type DtoType { get; } = typeof(WorkspaceLoggerDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceLoggerResource Instance { get; } = new();
}

public sealed record WorkspaceLoggerDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required LoggerContract Properties { get; init; }

    public sealed record LoggerContract
    {
        [JsonPropertyName("loggerType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LoggerType { get; init; }

        [JsonPropertyName("credentials")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonObject? Credentials { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("isBuffered")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? IsBuffered { get; init; }

        [JsonPropertyName("resourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ResourceId { get; init; }
    }
}
