using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ProductName : NonEmptyString
{
    private ProductName(string value) : base(value)
    {
    }

    public static ProductName From(string value) => new(value);
}

public sealed record ProductDisplayName : NonEmptyString
{
    private ProductDisplayName(string value) : base(value)
    {
    }

    public static ProductDisplayName From(string value) => new(value);
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

public static class Product
{
    private static readonly JsonSerializerOptions serializerOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("products")
               .AppendPath(productName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName) =>
        Service.GetUri(serviceProviderUri, serviceName)
               .AppendPath("products");

    public static ProductName GetNameFromFile(ProductInformationFile file)
    {
        var jsonObject = file.ReadAsJsonObject();
        var product = Deserialize(jsonObject);

        return ProductName.From(product.Name);
    }

    public static Models.Product Deserialize(JsonObject jsonObject) =>
        JsonSerializer.Deserialize<Models.Product>(jsonObject, serializerOptions) ?? throw new InvalidOperationException("Cannot deserialize JSON.");

    public static JsonObject Serialize(Models.Product product) =>
        JsonSerializer.SerializeToNode(product, serializerOptions)?.AsObject() ?? throw new InvalidOperationException("Cannot serialize to JSON.");

    public static async ValueTask<Models.Product> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, productName);
        var json = await getResource(uri, cancellationToken);
        return Deserialize(json);
    }

    public static IAsyncEnumerable<Models.Product> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName);
        return getResources(uri, cancellationToken).Select(Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, Models.Product product, CancellationToken cancellationToken)
    {
        var name = ProductName.From(product.Name);
        var uri = GetUri(serviceProviderUri, serviceName, name);
        var json = Serialize(product);
        await putResource(uri, json, cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, productName);
        await deleteResource(uri, cancellationToken);
    }
}