using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceTagResource : IResourceWithInformationFile, IChildResource
{
    private WorkspaceTagResource() { }

    public string FileName { get; } = "tagInformation.json";

    public string CollectionDirectoryName { get; } = "tags";

    public string SingularName { get; } = "tag";

    public string PluralName { get; } = "tags";

    public string CollectionUriPath { get; } = "tags";

    public Type DtoType { get; } = typeof(WorkspaceTagDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceTagResource Instance { get; } = new();
}

public sealed record WorkspaceTagDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required TagContract Properties { get; init; }

    public sealed record TagContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }
    }
}
