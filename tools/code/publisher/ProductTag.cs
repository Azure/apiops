using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ProductTag
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }

    private static async ValueTask Put(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactProductTags(files, configurationJson, serviceDirectory)
                .ForEachParallel(async product => await Put(product.ProductName, product.TagNames, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ProductName ProductName, ImmutableList<TagName> TagNames)> GetArtifactProductTags(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationProductTags(configurationJson);

        return GetProductTagsFiles(files, serviceDirectory)
                .Select(file =>
                {
                    var productName = GetProductName(file);
                    var tagNames = file.ReadAsJsonArray()
                                    .Choose(node => node as JsonObject)
                                    .Choose(tagJsonObject => tagJsonObject.TryGetStringProperty("name"))
                                    .Select(name => new TagName(name))
                                    .ToImmutableList();
                    return (ProductName: productName, TagNames: tagNames);
                })
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.ProductName,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.ProductName, configurationArtifact.TagNames));
    }

    private static IEnumerable<ProductTagsFile> GetProductTagsFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetTagsFile(file, serviceDirectory));
    }

    private static ProductTagsFile? TryGetTagsFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ProductTagsFile.Name) is false)
        {
            return null;
        }

        var productDirectory = Product.TryGetProductDirectory(file.Directory, serviceDirectory);

        return productDirectory is null
                ? null
                : new ProductTagsFile(productDirectory);
    }

    private static ProductName GetProductName(ProductTagsFile file)
    {
        return new(file.ProductDirectory.GetName());
    }

    private static IEnumerable<(ProductName ProductName, ImmutableList<TagName> TagNames)> GetConfigurationProductTags(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("products")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose<JsonObject, (ProductName ProductName, ImmutableList<TagName> TagNames)>(productJsonObject =>
                                {
                                    var productNameString = productJsonObject.TryGetStringProperty("name");
                                    if (productNameString is null)
                                    {
                                        return default;
                                    }

                                    var productName = new ProductName(productNameString);

                                    var tagsJsonArray = productJsonObject.TryGetJsonArrayProperty("tags");
                                    if (tagsJsonArray is null)
                                    {
                                        return default;
                                    }

                                    if (tagsJsonArray.Any() is false)
                                    {
                                        return (productName, ImmutableList.Create<TagName>());
                                    }

                                    // If tags are defined in configuration but none have a 'name' property, skip this resource
                                    var tagNames = tagsJsonArray.Choose(node => node as JsonObject)
                                                                    .Choose(tagJsonObject => tagJsonObject.TryGetStringProperty("name"))
                                                                    .Select(name => new TagName(name))
                                                                    .ToImmutableList();
                                    return tagNames.Any() ? (productName, tagNames) : default;
                                });
    }

    private static async ValueTask Put(ProductName productName, IReadOnlyCollection<TagName> tagNames, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var productUri = GetProductUri(productName, serviceUri);
        var productTagsUri = new ProductTagsUri(productUri);

        var existingTagNames = await listRestResources(productTagsUri.Uri, cancellationToken)
                                        .Select(tagJsonObject => tagJsonObject.GetStringProperty("name"))
                                        .Select(name => new TagName(name))
                                        .ToListAsync(cancellationToken);

        var tagNamesToPut = tagNames.Except(existingTagNames);
        var tagNamesToRemove = existingTagNames.Except(tagNames);

        await tagNamesToRemove.ForEachParallel(async tagName =>
        {
            logger.LogInformation("Removing tag {tagName} in product {productName}...", tagName, productName);
            await Delete(tagName, productUri, deleteRestResource, cancellationToken);
        }, cancellationToken);

        await tagNamesToPut.ForEachParallel(async tagName =>
        {
            logger.LogInformation("Putting tag {tagName} in product {productName}...", tagName, productName);
            await Put(tagName, productUri, putRestResource, cancellationToken);
        }, cancellationToken);
    }

    private static ProductUri GetProductUri(ProductName productName, ServiceUri serviceUri)
    {
        var productsUri = new ProductsUri(serviceUri);
        return new ProductUri(productName, productsUri);
    }

    private static async ValueTask Delete(TagName tagName, ProductUri productUri, DeleteRestResource deleteRestResource, CancellationToken cancellationToken)
    {
        var productTagsUri = new ProductTagsUri(productUri);
        var productTagUri = new ProductTagUri(tagName, productTagsUri);

        await deleteRestResource(productTagUri.Uri, cancellationToken);
    }

    private static async ValueTask Put(TagName tagName, ProductUri productUri, PutRestResource putRestResource, CancellationToken cancellationToken)
    {
        var productTagsUri = new ProductTagsUri(productUri);
        var productTagUri = new ProductTagUri(tagName, productTagsUri);

        await putRestResource(productTagUri.Uri, new JsonObject(), cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }
}