using System.Text.Json.Serialization;

namespace common.Models;

public sealed record ApiVersionSet([property: JsonPropertyName("name")] string Name,
                         [property: JsonPropertyName("properties")] ApiVersionSet.ApiVersionSetCreateOrUpdateProperties Properties)
{
    public sealed record ApiVersionSetCreateOrUpdateProperties([property: JsonPropertyName("versioningScheme")] string VersioningScheme,
                                                     [property: JsonPropertyName("displayName")] string DisplayName)
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("versionHeaderName")]
        public string? VersionHeaderName { get; init; }

        [JsonPropertyName("versionQueryName")]
        public string? VersionQueryName { get; init; }

    }
}