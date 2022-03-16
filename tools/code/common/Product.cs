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

    public ServiceDirectory ServiceDirectory { get; }

    private ProductsDirectory(ServiceDirectory serviceDirectory) : base(serviceDirectory.Path.Append(name))
    {
        ServiceDirectory = serviceDirectory;
    }

    public static ProductsDirectory From(ServiceDirectory serviceDirectory) => new(serviceDirectory);

    public static ProductsDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory) =>
        name.Equals(directory?.Name) && serviceDirectory.PathEquals(directory.Parent)
        ? new(serviceDirectory)
        : null;
}

public sealed record ProductDirectory : DirectoryRecord
{
    public ProductsDirectory ProductsDirectory { get; }
    public ProductDisplayName ProductDisplayName { get; }

    private ProductDirectory(ProductsDirectory productsDirectory, ProductDisplayName productDisplayName) : base(productsDirectory.Path.Append(productDisplayName))
    {
        ProductsDirectory = productsDirectory;
        ProductDisplayName = productDisplayName;
    }

    public static ProductDirectory From(ProductsDirectory productsDirectory, ProductDisplayName productDisplayName) => new(productsDirectory, productDisplayName);

    public static ProductDirectory? TryFrom(ServiceDirectory serviceDirectory, DirectoryInfo? directory)
    {
        var parentDirectory = directory?.Parent;
        if (parentDirectory is not null)
        {
            var productsDirectory = ProductsDirectory.TryFrom(serviceDirectory, parentDirectory);

            return productsDirectory is null ? null : From(productsDirectory, ProductDisplayName.From(directory!.Name));
        }
        else
        {
            return null;
        }
    }
}

public sealed record ProductInformationFile : FileRecord
{
    private static readonly string name = "productInformation.json";

    public ProductDirectory ProductDirectory { get; }

    private ProductInformationFile(ProductDirectory productDirectory) : base(productDirectory.Path.Append(name))
    {
        ProductDirectory = productDirectory;
    }

    public static ProductInformationFile From(ProductDirectory productDirectory) => new(productDirectory);

    public static ProductInformationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name))
        {
            var productDirectory = ProductDirectory.TryFrom(serviceDirectory, file.Directory);

            return productDirectory is null ? null : new(productDirectory);
        }
        else
        {
            return null;
        }
    }
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