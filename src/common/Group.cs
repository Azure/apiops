using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record GroupResource : IResourceWithInformationFile
{
    private GroupResource() { }

    public string FileName { get; } = "groupInformation.json";

    public string CollectionDirectoryName { get; } = "groups";

    public string SingularName { get; } = "group";

    public string PluralName { get; } = "groups";

    public string CollectionUriPath { get; } = "groups";

    public Type DtoType { get; } = typeof(GroupDto);

    public static GroupResource Instance { get; } = new();

    public static ResourceName Administrators { get; } = ResourceName.From("Administrators").IfErrorThrow();
    public static ResourceName Developers { get; } = ResourceName.From("Developers").IfErrorThrow();
    public static ResourceName Guests { get; } = ResourceName.From("Guests").IfErrorThrow();
}

public sealed record GroupDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required GroupContract Properties { get; init; }

    public sealed record GroupContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("externalId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ExternalId { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }
    }
}