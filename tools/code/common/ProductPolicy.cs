using System;
using System.IO;
using System.Text.Json.Nodes;

namespace common;

public sealed record ProductPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";

    public ProductDirectory ProductDirectory { get; }

    private ProductPolicyFile(ProductDirectory productDirectory) : base(productDirectory.Path.Append(name))
    {
        ProductDirectory = productDirectory;
    }

    public static ProductPolicyFile From(ProductDirectory productDirectory) => new(productDirectory);

    public static ProductPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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

public sealed record ProductPolicyUri : UriRecord
{
    public ProductPolicyUri(Uri value) : base(value)
    {
    }

    public static ProductPolicyUri From(ProductUri productUri) =>
        new(UriExtensions.AppendPath(productUri, "policies").AppendPath("policy").SetQueryParameter("format", "rawxml"));
}

public record ProductPolicy
{
    public static string GetFromJson(JsonObject jsonObject) => jsonObject.GetJsonObjectProperty("properties").GetStringProperty("value");
}