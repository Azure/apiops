using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record TagResource : IResourceWithInformationFile
{
    private TagResource() { }

    public string FileName { get; } = "tagInformation.json";

    public string CollectionDirectoryName { get; } = "tags";

    public string SingularName { get; } = "tag";

    public string PluralName { get; } = "tags";

    public string CollectionUriPath { get; } = "tags";

    public Type DtoType { get; } = typeof(TagDto);

    public static TagResource Instance { get; } = new();
}

public sealed record TagDto
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