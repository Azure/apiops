using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceVersionSetResource : IResourceWithInformationFile, IChildResource
{
    private WorkspaceVersionSetResource() { }

    public string FileName { get; } = "versionSetInformation.json";

    public string CollectionDirectoryName { get; } = "version sets";

    public string SingularName { get; } = "version set";

    public string PluralName { get; } = "version sets";

    public string CollectionUriPath { get; } = "apiVersionSets";

    public Type DtoType { get; } = typeof(WorkspaceVersionSetDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceVersionSetResource Instance { get; } = new();
}

public sealed record WorkspaceVersionSetDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required VersionSetContract Properties { get; init; }

    public sealed record VersionSetContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("versioningScheme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersioningScheme { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("versionHeaderName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersionHeaderName { get; init; }

        [JsonPropertyName("versionQueryName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersionQueryName { get; init; }
    }
}
