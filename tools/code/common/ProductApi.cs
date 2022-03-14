using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ProductApisFile : FileRecord
{
    private static readonly string name = "apis.json";
    private readonly ProductsDirectory productsDirectory;
    private readonly ProductDisplayName productDisplayName;

    private ProductApisFile(ProductsDirectory productsDirectory, ProductDisplayName productDisplayName)
        : base(productsDirectory.Path.Append(productDisplayName).Append(name))
    {
        this.productsDirectory = productsDirectory;
        this.productDisplayName = productDisplayName;
    }

    public ProductInformationFile GetProductInformationFile() => ProductInformationFile.From(productsDirectory, productDisplayName);

    public static ProductApisFile From(ProductsDirectory productsDirectory, ProductDisplayName displayName)
        => new(productsDirectory, displayName);

    public static ProductApisFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (name.Equals(file.Name) is false)
        {
            return null;
        }

        var directory = file.Directory;
        if (directory is null)
        {
            return null;
        }

        var productsDirectory = ProductsDirectory.TryFrom(serviceDirectory, directory.Parent);
        return productsDirectory is null
            ? null
            : new(productsDirectory, ProductDisplayName.From(directory.Name));
    }
}

public sealed record ProductApiUri : UriRecord
{
    public ProductApiUri(Uri value) : base(value)
    {
    }

    public static ProductApiUri From(ProductUri productUri, ApiName apiName) =>
        new(UriExtensions.AppendPath(productUri, "apis").AppendPath(apiName));
}

public sealed record ProductApi([property: JsonPropertyName("name")] string Name)
{
    public JsonObject ToJsonObject() =>
        JsonSerializer.SerializeToNode(this)?.AsObject() ?? throw new InvalidOperationException("Could not serialize object.");

    public static ProductApi FromJsonObject(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<ProductApi>(jsonObject) ?? throw new InvalidOperationException("Could not deserialize object.");

    public static Uri GetListByProductUri(ProductUri productUri) => UriExtensions.AppendPath(productUri, "apis");
}