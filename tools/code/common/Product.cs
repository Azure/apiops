using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common;

public sealed record ProductsUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductsUri(ServiceUri serviceUri)
    {
        Uri = serviceUri.AppendPath("products");
    }
}

public sealed record ProductsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "products";

    public ArtifactPath Path { get; }

    public ServiceDirectory ServiceDirectory { get; }

    public ProductsDirectory(ServiceDirectory serviceDirectory)
    {
        Path = serviceDirectory.Path.Append(Name);
        ServiceDirectory = serviceDirectory;
    }
}

public sealed record ProductName
{
    private readonly string value;

    public ProductName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Product name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ProductUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductUri(ProductName productName, ProductsUri productsUri)
    {
        Uri = productsUri.AppendPath(productName.ToString());
    }
}

public sealed record ProductDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public ProductsDirectory ProductsDirectory { get; }

    public ProductDirectory(ProductName productName, ProductsDirectory productsDirectory)
    {
        Path = productsDirectory.Path.Append(productName.ToString());
        ProductsDirectory = productsDirectory;
    }
}

public sealed record ProductInformationFile : IArtifactFile
{
    public static string Name { get; } = "productInformation.json";

    public ArtifactPath Path { get; }

    public ProductDirectory ProductDirectory { get; }

    public ProductInformationFile(ProductDirectory productDirectory)
    {
        Path = productDirectory.Path.Append(Name);
        ProductDirectory = productDirectory;
    }
}

public sealed record ProductModel
{
    public required string Name { get; init; }

    public required ProductContractProperties Properties { get; init; }

    public sealed record ProductContractProperties
    {
        public bool? ApprovalRequired { get; init; }
        public string? Description { get; init; }
        public string? DisplayName { get; init; }
        public ProductStateOption? State { get; init; }
        public bool? SubscriptionRequired { get; init; }
        public int? SubscriptionsLimit { get; init; }
        public string? Terms { get; init; }

        public JsonObject Serialize() =>
            new JsonObject()
                .AddPropertyIfNotNull("approvalRequired", ApprovalRequired)
                .AddPropertyIfNotNull("description", Description)
                .AddPropertyIfNotNull("displayName", DisplayName)
                .AddPropertyIfNotNull("state", State?.Serialize())
                .AddPropertyIfNotNull("subscriptionRequired", SubscriptionRequired)
                .AddPropertyIfNotNull("subscriptionsLimit", SubscriptionsLimit)
                .AddPropertyIfNotNull("terms", Terms);

        public static ProductContractProperties Deserialize(JsonObject jsonObject) =>
            new()
            {
                ApprovalRequired = jsonObject.TryGetBoolProperty("approvalRequired"),
                Description = jsonObject.TryGetStringProperty("description"),
                DisplayName = jsonObject.TryGetStringProperty("displayName"),
                State = jsonObject.TryGetProperty("state")
                                  .Map(ProductStateOption.Deserialize),
                SubscriptionRequired = jsonObject.TryGetBoolProperty("subscriptionRequired"),
                SubscriptionsLimit = jsonObject.TryGetIntProperty("subscriptionsLimit"),
                Terms = jsonObject.TryGetStringProperty("terms")
            };

        public sealed record ProductStateOption
        {
            private readonly string value;

            private ProductStateOption(string value)
            {
                this.value = value;
            }

            public static ProductStateOption Published => new("published");
            public static ProductStateOption NotPublished => new("notPublished");

            public override string ToString() => value;

            public JsonNode Serialize() => JsonValue.Create(ToString()) ?? throw new JsonException("Value cannot be null.");

            public static ProductStateOption Deserialize(JsonNode node) =>
                node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var value)
                    ? value switch
                    {
                        _ when nameof(Published).Equals(value, StringComparison.OrdinalIgnoreCase) => Published,
                        _ when nameof(NotPublished).Equals(value, StringComparison.OrdinalIgnoreCase) => NotPublished,
                        _ => throw new JsonException($"'{value}' is not a valid {nameof(ProductStateOption)}.")
                    }
                        : throw new JsonException("Node must be a string JSON value.");
        }
    }

    public JsonObject Serialize() =>
        new JsonObject()
            .AddProperty("properties", Properties.Serialize());

    public static ProductModel Deserialize(ProductName name, JsonObject jsonObject) =>
        new()
        {
            Name = jsonObject.TryGetStringProperty("name") ?? name.ToString(),
            Properties = jsonObject.GetJsonObjectProperty("properties")
                                   .Map(ProductContractProperties.Deserialize)!
        };
}