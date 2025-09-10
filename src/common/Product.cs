using System;
using System.Text.Json.Serialization;

namespace common;

public sealed record ProductResource : IResourceWithInformationFile
{
    private ProductResource() { }

    public string FileName { get; } = "productInformation.json";

    public string CollectionDirectoryName { get; } = "products";

    public string SingularName { get; } = "product";

    public string PluralName { get; } = "products";

    public string CollectionUriPath { get; } = "products";

    public Type DtoType { get; } = typeof(ProductDto);

    public static ProductResource Instance { get; } = new();
}

public sealed record ProductDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ProductContract Properties { get; init; }

    public record ProductContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("approvalRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ApprovalRequired { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("subscriptionsLimit")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? SubscriptionsLimit { get; init; }

        [JsonPropertyName("terms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Terms { get; init; }
    }
}