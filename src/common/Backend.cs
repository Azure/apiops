using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record BackendResource : IResourceWithInformationFile
{
    private BackendResource() { }

    public string FileName { get; } = "backendInformation.json";

    public string CollectionDirectoryName { get; } = "backends";

    public string SingularName { get; } = "backend";

    public string PluralName { get; } = "backends";

    public string CollectionUriPath { get; } = "backends";

    public Type DtoType { get; } = typeof(BackendDto);

    public static BackendResource Instance { get; } = new();
}

public sealed record BackendDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required BackendContract Properties { get; init; }

    public sealed record BackendContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("protocol")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Protocol { get; init; }

        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Title { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
#pragma warning disable CA1056 // URI-like properties should not be strings
        public string? Url { get; init; }
#pragma warning restore CA1056 // URI-like properties should not be strings
    }
}
