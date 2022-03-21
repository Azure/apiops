using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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

public static class ProductApi
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, ApiName apiName) =>
        Product.GetUri(serviceProviderUri, serviceName, productName)
               .AppendPath("apis")
               .AppendPath(apiName);

    internal static Uri ListUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName) =>
        Product.GetUri(serviceProviderUri, serviceName, productName)
               .AppendPath("apis");

    public static ImmutableList<ApiName> ListFromFile(ProductApisFile file) =>
        file.ReadAsJsonArray()
            .Where(node => node is not null)
            .Select(node => node!.AsObject())
            .Select(jsonObject => jsonObject.GetStringProperty("name"))
            .Select(ApiName.From)
            .ToImmutableList();

    public static IAsyncEnumerable<Models.Api> List(Func<Uri, CancellationToken, IAsyncEnumerable<JsonObject>> getResources, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, CancellationToken cancellationToken)
    {
        var uri = ListUri(serviceProviderUri, serviceName, productName);
        return getResources(uri, cancellationToken).Select(Api.Deserialize);
    }

    public static async ValueTask Put(Func<Uri, JsonObject, CancellationToken, ValueTask> putResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, productName, apiName);
        await putResource(uri, new JsonObject(), cancellationToken);
    }

    public static async ValueTask Delete(Func<Uri, CancellationToken, ValueTask> deleteResource, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ProductName productName, ApiName apiName, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, productName, apiName);
        await deleteResource(uri, cancellationToken);
    }
}