using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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

public static class ProductPolicy
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName) =>
        Product.GetUri(serviceProviderUri, serviceName, productName)
               .AppendPath("policies")
               .AppendPath("policy")
               .SetQueryParameter("format", "rawxml");

    public static async ValueTask<string?> TryGet(Func<Uri, CancellationToken, ValueTask<JsonObject?>> tryGetResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, productName);
        var json = await tryGetResource(uri, cancellationToken);

        return json?.GetJsonObjectProperty("properties")
                    .GetStringProperty("value");
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, string policyText, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["format"] = "rawxml",
                ["value"] = policyText
            }
        };

        var uri = GetUri(serviceProviderUri, serviceName, productName);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, productName);
        await deleteResource(uri, cancellationToken);
    }
}