using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace common;

public sealed record SubscriptionResource : IResourceWithReference
{
    private SubscriptionResource() { }

    public string FileName { get; } = "subscriptionInformation.json";

    public string CollectionDirectoryName { get; } = "subscriptions";

    public string SingularName { get; } = "subscription";

    public string PluralName { get; } = "subscriptions";

    public string CollectionUriPath { get; } = "subscriptions";

    public ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(ProductResource.Instance, nameof(SubscriptionDto.Properties.Scope))
                           .Add(ApiResource.Instance, nameof(SubscriptionDto.Properties.Scope));

    public Type DtoType { get; } = typeof(SubscriptionDto);

    public static SubscriptionResource Instance { get; } = new();

    public static ResourceName Master { get; } = ResourceName.From("master").IfErrorThrow();
}

public sealed record SubscriptionDto
{
    [JsonPropertyName("properties")]
    public required SubscriptionContract Properties { get; init; }

    public sealed record SubscriptionContract
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }

        [JsonPropertyName("allowTracing")]
        public bool? AllowTracing { get; init; }

        [JsonPropertyName("ownerId")]
        public string? OwnerId { get; init; }

        [JsonPropertyName("primaryKey")]
        public string? PrimaryKey { get; init; }

        [JsonPropertyName("secondaryKey")]
        public string? SecondaryKey { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }
    }
}