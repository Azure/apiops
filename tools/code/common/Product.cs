using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ProductName : NonEmptyString
{
    private ProductName(string value) : base(value)
    {
    }

    public static ProductName From(string value) => new(value);

    public static ProductName From(ProductInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var product = Product.FromJsonObject(jsonObject);

        return new ProductName(product.Name);
    }
}

public sealed record ProductDisplayName : NonEmptyString
{
    private ProductDisplayName(string value) : base(value)
    {
    }

    public static ProductDisplayName From(string value) => new(value);

    public static ProductDisplayName From(ProductInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var product = Product.FromJsonObject(jsonObject);

        return new ProductDisplayName(product.Properties.DisplayName);
    }
}

public sealed record ProductUri : UriRecord
{
    public ProductUri(Uri value) : base(value)
    {
    }

    public static ProductUri From(ServiceUri serviceUri, ProductName productName) =>
        new(UriExtensions.AppendPath(serviceUri, "products").AppendPath(productName));
}

public sealed record ProductsDirectory : DirectoryRecord
{
    private static readonly string name = "products";

    private ProductsDirectory(RecordPath path) : base(path)
    {
    }

    public static ProductsDirectory From(ServiceDirectory serviceDirectory) =>
        new(serviceDirectory.Path.Append(name));

    public static ProductsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.Path.PathEquals(directory.Parent?.FullName)
        ? new(RecordPath.From(directory.FullName))
        : null;
}

public sealed record ProductInformationFile : FileRecord
{
    private static readonly string name = "productInformation.json";

    public ProductInformationFile(RecordPath path) : base(path)
    {
    }

    public static ProductInformationFile From(ProductsDirectory productsDirectory, ProductDisplayName displayName)
        => new(productsDirectory.Path.Append(displayName)
                                        .Append(name));

    public static ProductInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file) =>
        name.Equals(file.Name) && ProductsDirectory.TryFrom(serviceDirectory, file.Directory?.Parent) is not null
        ? new(RecordPath.From(file.FullName))
        : null;
}

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

    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static Product FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Product>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByServiceUri(ServiceUri serviceUri) => UriExtensions.AppendPath(serviceUri, "products");
}