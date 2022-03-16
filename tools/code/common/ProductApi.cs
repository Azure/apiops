using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace common;

public sealed record ProductApisFile : FileRecord
{
    private static readonly string name = "apis.json";

    public ProductDirectory ProductDirectory { get; }

    private ProductApisFile(ProductDirectory productDirectory) : base(productDirectory.Path.Append(name))
    {
        ProductDirectory = productDirectory;
    }

    public static ProductApisFile From(ProductDirectory productDirectory) => new(productDirectory);

    public static ProductApisFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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