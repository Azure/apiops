using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Gateway([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Gateway.GatewayContractProperties Properties)
{
    public record GatewayContractProperties
    {
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("locationData")]
        public ResourceLocationDataContract? LocationData { get; init; }
    }

    public record ResourceLocationDataContract([property: JsonPropertyName("name")] string Name)
    {
        [JsonPropertyName("city")]
        public string? City { get; init; }

        [JsonPropertyName("countryOrRegion")]
        public string? CountryOrRegion { get; init; }
        [JsonPropertyName("district")]
        public string? District { get; init; }
    }
}
