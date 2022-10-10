using System.Text.Json.Serialization;

namespace common.Models;

public sealed record PolicyFragment([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] PolicyFragment.PolicyFragmentContractProperties Properties)
{
    public record PolicyFragmentContractProperties
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }
        [JsonPropertyName("format")]
        public string? Format { get; init; }
        [JsonPropertyName("value")]
        public string? Value { get; init; }
    }
}
