using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ProductPolicyFile : FileRecord
{
    private static readonly string name = "policy.xml";
    private readonly ProductsDirectory productsDirectory;
    private readonly ProductDisplayName productDisplayName;

    private ProductPolicyFile(ProductsDirectory productsDirectory, ProductDisplayName productDisplayName)
        : base(productsDirectory.Path.Append(productDisplayName).Append(name))
    {
        this.productsDirectory = productsDirectory;
        this.productDisplayName = productDisplayName;
    }

    public async Task<JsonObject> ToJsonObject(CancellationToken cancellationToken)
    {
        var policyText = await File.ReadAllTextAsync(Path, cancellationToken);
        var propertiesJson = new JsonObject().AddProperty("format", "rawxml")
                                             .AddProperty("value", policyText);

        return new JsonObject().AddProperty("properties", propertiesJson);
    }

    public ProductInformationFile GetProductInformationFile() => ProductInformationFile.From(productsDirectory, productDisplayName);

    public static ProductPolicyFile From(ProductsDirectory productsDirectory, ProductDisplayName displayName)
        => new(productsDirectory, displayName);

    public static ProductPolicyFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
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