using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class Product
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetProductInformationFiles(files, serviceDirectory)
                .Select(GetProductName)
                .ForEachParallel(async productName => await Delete(productName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<ProductInformationFile> GetProductInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetProductInformationFile(file, serviceDirectory));
    }

    private static ProductInformationFile? TryGetProductInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ProductInformationFile.Name) is false)
        {
            return null;
        }

        var productDirectory = TryGetProductDirectory(file.Directory, serviceDirectory);

        return productDirectory is null
                ? null
                : new ProductInformationFile(productDirectory);
    }

    public static ProductDirectory? TryGetProductDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var productsDirectory = TryGetProductsDirectory(directory.Parent, serviceDirectory);
        if (productsDirectory is null)
        {
            return null;
        }

        var productName = new ProductName(directory.Name);
        return new ProductDirectory(productName, productsDirectory);
    }

    private static ProductsDirectory? TryGetProductsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(ProductsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new ProductsDirectory(serviceDirectory);
    }

    private static ProductName GetProductName(ProductInformationFile file)
    {
        return new(file.ProductDirectory.GetName());
    }

    private static async ValueTask Delete(ProductName productName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetProductUri(productName, serviceUri);

        logger.LogInformation("Deleting product {productName}...", productName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    public static ProductUri GetProductUri(ProductName productName, ServiceUri serviceUri)
    {
        var productsUri = new ProductsUri(serviceUri);
        return new ProductUri(productName, productsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutProduct(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ProductName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationProducts(configurationJson);

        return GetProductInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetProductName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(ProductName Name, JsonObject Json)> GetConfigurationProducts(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("products")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (ProductName, JsonObject)?
                                            : (new ProductName(name), jsonObject);
                                });
    }

    private static async ValueTask PutProduct(ProductName productName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product {productName}...", productName);

        var uri = GetProductUri(productName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}