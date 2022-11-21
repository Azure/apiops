using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common
{
    public sealed record SubscriptionsUri : IArtifactUri
    {
        public Uri Uri { get; }

        public SubscriptionsUri(ServiceUri serviceUri)
        {
            Uri = serviceUri.AppendPath("subscriptions");
        }
    }
    public sealed record SubscriptionsDirectory : IArtifactDirectory
    {
        public static string Name { get; } = "subscriptions";

        public ArtifactPath Path { get; }

        public ServiceDirectory ServiceDirectory { get; }

        public SubscriptionsDirectory(ServiceDirectory serviceDirectory)
        {
            Path = serviceDirectory.Path.Append(Name);
            ServiceDirectory = serviceDirectory;
        }
    }

    public sealed record SubscriptionName
    {
        private readonly string value;

        public SubscriptionName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Subscription name cannot be null or whitespace.", nameof(value));
            }

            this.value = value;
        }

        public override string ToString() => value;
    }

    public sealed record SubscriptionUri : IArtifactUri
    {
        public Uri Uri { get; }

        public SubscriptionUri(SubscriptionName subscriptionName, SubscriptionsUri subscriptionsUri)
        {
            Uri = subscriptionsUri.AppendPath(subscriptionName.ToString());
        }
    }

    public sealed record SubscriptionDirectory : IArtifactDirectory
    {
        public ArtifactPath Path { get; }

        public SubscriptionsDirectory SubscriptionsDirectory { get; }

        public SubscriptionDirectory(SubscriptionName subscriptionName, SubscriptionsDirectory subscriptionsDirectory)
        {
            Path = subscriptionsDirectory.Path.Append(subscriptionName.ToString());
            SubscriptionsDirectory = subscriptionsDirectory;
        }
    }

    public sealed record SubscriptionInformationFile : IArtifactFile
    {
        public static string Name { get; } = "subscriptionInformation.json";

        public ArtifactPath Path { get; }

        public SubscriptionDirectory SubscriptionDirectory { get; }

        public SubscriptionInformationFile(SubscriptionDirectory subscriptionDirectory)
        {
            Path = subscriptionDirectory.Path.Append(Name);
            SubscriptionDirectory = subscriptionDirectory;
        }
    }

    public sealed record SubscriptionModel
    {
        public required string Name { get; init; }

        public required SubscriptionContractProperties Properties { get; init; }

        public sealed record SubscriptionContractProperties
        {
            public string? DisplayName { get; init; }
            public bool? AllowTracing { get; init; }
            public string? OwnerId { get; init; }
            public string? PrimaryKey { get; init; }
            public string? Scope { get; init; }
            public string? SecondaryKey { get; init; }
            public string? State { get; init; }

            public JsonObject Serialize() =>
                new JsonObject()
                    .AddPropertyIfNotNull("displayName", DisplayName)
                    .AddPropertyIfNotNull("allowTracing", AllowTracing)
                    .AddPropertyIfNotNull("ownerId", OwnerId)
                    .AddPropertyIfNotNull("primaryKey", PrimaryKey)
                    .AddPropertyIfNotNull("scope", GetGenericSubscriptionScope(Scope))
                    .AddPropertyIfNotNull("secondaryKey", SecondaryKey)
                    .AddPropertyIfNotNull("state", State);

            public static SubscriptionContractProperties Deserialize(JsonObject jsonObject) =>
                new()
                {
                    DisplayName = jsonObject.TryGetStringProperty("displayName"),
                    AllowTracing = jsonObject.TryGetBoolProperty("allowTracing"),
                    OwnerId = jsonObject.TryGetStringProperty("ownerId"),
                    PrimaryKey = jsonObject.TryGetStringProperty("primaryKey"),
                    Scope = jsonObject.TryGetStringProperty("scope"),
                    SecondaryKey = jsonObject.TryGetStringProperty("secondaryKey"),
                    State = jsonObject.TryGetStringProperty("state")
                };
        }
        public static string GetGenericSubscriptionScope(string? fullScope)
        {
            var splittedScope = fullScope is not null ? fullScope.Split('/').Select((uriPart, index) => new { uriPart, index }) : throw new ArgumentNullException("Api Scope cannot be null");
            var splittedSubscriptionScope = splittedScope.Where(split => split.index >= splittedScope.Count() - 2).Select(split => split.uriPart).ToArray();
            if (!splittedSubscriptionScope.First().Equals("apis")
                && !splittedSubscriptionScope.First().Equals("products"))
            {
                if (splittedSubscriptionScope.Last().Equals("apis"))
                {
                    return "/apis";
                }
                throw new ArgumentException("Subscription scope should be one of '/apis', '/apis/{apiId}', '/products/{productId}'");
            }
            return string.Format("/{0}", string.Join('/', splittedSubscriptionScope));
        }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddProperty("properties", Properties.Serialize());

        public static SubscriptionModel Deserialize(SubscriptionName name, JsonObject jsonObject) =>
            new()
            {
                Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
                Properties = jsonObject.GetJsonObjectProperty("properties")
                                       .Map(SubscriptionContractProperties.Deserialize)!
            };

    }
}
