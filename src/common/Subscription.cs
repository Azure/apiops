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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required SubscriptionContract Properties { get; init; }

    public sealed record SubscriptionContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("scope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Scope { get; init; }

        [JsonPropertyName("allowTracing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? AllowTracing { get; init; }

        [JsonPropertyName("ownerId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? OwnerId { get; init; }

        [JsonPropertyName("primaryKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? PrimaryKey { get; init; }

        [JsonPropertyName("secondaryKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SecondaryKey { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }
    }
}