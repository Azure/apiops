using System.Text.Json.Serialization;

namespace common.Models;

public sealed record NamedValue([property: JsonPropertyName("name")] string Name,
                                [property: JsonPropertyName("properties")] NamedValue.NamedValueCreateContractProperties Properties)
{
    public sealed record NamedValueCreateContractProperties([property: JsonPropertyName("displayName")] string DisplayName)
    {
        [JsonPropertyName("keyVault")]
        public KeyVaultContractCreateProperties? KeyVault { get; init; }
        [JsonPropertyName("secret")]
        public bool? Secret { get; init; }
        [JsonPropertyName("tags")]
        public string[]? Tags { get; init; }
        [JsonPropertyName("value")]
        public string? Value { get; init; }
    }

    public sealed record KeyVaultContractCreateProperties
    {
        [JsonPropertyName("identityClientId")]
        public string? IdentityClientId { get; init; }
        [JsonPropertyName("secretIdentifier")]
        public string? SecretIdentifier { get; init; }
    }
}