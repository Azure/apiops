namespace codegen.resources;

public sealed record Product : IResourceWithName, IResourceWithInformationFile
{
    public static Product Instance { get; } = new Product();

    public string InformationFileType { get; } = "ProductInformationFile";

    public string InformationFileName { get; } = "productInformation.json";

    public string DtoType { get; } = "ProductDto";

    public string DtoCode { get; } =
"""
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
""";

    public string CollectionDirectoryType { get; } = "ProductsDirectory";

    public string CollectionDirectoryName { get; } = "products";

    public string DirectoryType { get; } = "ProductDirectory";

    public string NameType { get; } = "ProductName";

    public string NameParameter { get; } = "productName";

    public string SingularDescription { get; } = "Product";

    public string PluralDescription { get; } = "Products";

    public string LoggerSingularDescription { get; } = "product";

    public string LoggerPluralDescription { get; } = "products";

    public string CollectionUriType { get; } = "ProductsUri";

    public string CollectionUriPath { get; } = "products";

    public string UriType { get; } = "ProductUri";
}
