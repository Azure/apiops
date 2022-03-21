using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Logger([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Logger.LoggerContractProperties Properties)
{
    public record LoggerContractProperties
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }
        [JsonPropertyName("isBuffered")]
        public bool? IsBuffered { get; init; }
        [JsonPropertyName("loggerType")]
        public string? LoggerType { get; init; }
        [JsonPropertyName("resourceId")]
        public string? ResourceId { get; init; }
        [JsonPropertyName("credentials")]
        public Credentials? Credentials { get; init; }
    }

    public record Credentials
    {
        [JsonPropertyName("instrumentationKey")]
        public string? InstrumentationKey { get; init; }
        [JsonPropertyName("name")]
        public string? Name { get; init; }
        [JsonPropertyName("connectionString")]
        public string? ConnectionString { get; init; }
    }
}
