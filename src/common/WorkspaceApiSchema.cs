using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceApiSchemaResource : IResourceWithDto, IChildResource
{
    private WorkspaceApiSchemaResource() { }

    public string CollectionDirectoryName { get; } = "schemas";

    public string SingularName { get; } = "schema";

    public string PluralName { get; } = "schemas";

    public string CollectionUriPath { get; } = "schemas";

    public Type DtoType { get; } = typeof(WorkspaceApiSchemaDto);

    public IResource Parent { get; } = WorkspaceApiResource.Instance;

    public static WorkspaceApiSchemaResource Instance { get; } = new();
}

public sealed record WorkspaceApiSchemaDto
{
    [JsonPropertyName("properties")]
    public required WorkspaceApiSchemaContract Properties { get; init; }

    public sealed record WorkspaceApiSchemaContract
    {
        [JsonPropertyName("contentType")]
        public required string ContentType { get; init; }

        [JsonPropertyName("document")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public WorkspaceApiSchemaDocument? Document { get; init; }
    }

    public sealed record WorkspaceApiSchemaDocument
    {
        [JsonPropertyName("components")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public object? Components { get; init; }

        [JsonPropertyName("definitions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public object? Definitions { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }
}
