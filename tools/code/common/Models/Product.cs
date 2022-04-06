using System.Text.Json.Serialization;

namespace common.Models;

public sealed record Product([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("properties")] Product.ProductContractProperties Properties)
{
    public record ProductContractProperties([property: JsonPropertyName("displayName")] string DisplayName)
    {
        [JsonPropertyName("approvalRequired")]
        public bool? ApprovalRequired { get; init; }
        [JsonPropertyName("description")]
        public string? Description { get; init; }
        [JsonPropertyName("state")]
        public string? State { get; init; }
        [JsonPropertyName("subscriptionRequired")]
        public bool? SubscriptionRequired { get; init; }
        [JsonPropertyName("subscriptionsLimit")]
        public int? SubscriptionsLimit { get; init; }
        [JsonPropertyName("terms")]
        public string? Terms { get; init; }
    }
}