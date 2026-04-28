using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace common;

public sealed record WorkspaceSubscriptionResource : IResourceWithReference, IChildResource
{
    private WorkspaceSubscriptionResource() { }

    public string FileName { get; } = "subscriptionInformation.json";

    public string CollectionDirectoryName { get; } = "subscriptions";

    public string SingularName { get; } = "subscription";

    public string PluralName { get; } = "subscriptions";

    public string CollectionUriPath { get; } = "subscriptions";

    public ImmutableDictionary<IResource, string> OptionalReferencedResourceDtoProperties { get; } =
        ImmutableDictionary.Create<IResource, string>()
                           .Add(WorkspaceProductResource.Instance, nameof(WorkspaceSubscriptionDto.Properties.Scope))
                           .Add(WorkspaceApiResource.Instance, nameof(WorkspaceSubscriptionDto.Properties.Scope));

    public Type DtoType { get; } = typeof(WorkspaceSubscriptionDto);

    public IResource Parent { get; } = WorkspaceResource.Instance;

    public static WorkspaceSubscriptionResource Instance { get; } = new();
}

public sealed record WorkspaceSubscriptionDto
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
