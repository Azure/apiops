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

internal static class ProductGroup
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }

    private static async ValueTask Put(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactProductGroups(files, configurationJson, serviceDirectory)
                .ForEachParallel(async product => await Put(product.ProductName, product.GroupNames, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ProductName ProductName, ImmutableList<GroupName> GroupNames)> GetArtifactProductGroups(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationProductGroups(configurationJson);

        return GetProductGroupsFiles(files, serviceDirectory)
                .Select(file =>
                {
                    var productName = GetProductName(file);
                    var groupNames = file.ReadAsJsonArray()
                                        .Choose(node => node as JsonObject)
                                        .Choose(groupJsonObject => groupJsonObject.TryGetStringProperty("name"))
                                        .Select(name => new GroupName(name))
                                        .ToImmutableList();
                    return (ProductName: productName, GroupNames: groupNames);
                })
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.ProductName,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.ProductName, configurationArtifact.GroupNames));
    }

    private static IEnumerable<ProductGroupsFile> GetProductGroupsFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetGroupsFile(file, serviceDirectory));
    }

    private static ProductGroupsFile? TryGetGroupsFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ProductGroupsFile.Name) is false)
        {
            return null;
        }

        var productDirectory = Product.TryGetProductDirectory(file.Directory, serviceDirectory);

        return productDirectory is null
                ? null
                : new ProductGroupsFile(productDirectory);
    }

    private static ProductName GetProductName(ProductGroupsFile file)
    {
        return new(file.ProductDirectory.GetName());
    }

    private static IEnumerable<(ProductName ProductName, ImmutableList<GroupName> GroupNames)> GetConfigurationProductGroups(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("products")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose<JsonObject, (ProductName ProductName, ImmutableList<GroupName> GroupNames)>(productJsonObject =>
                                {
                                    var productNameString = productJsonObject.TryGetStringProperty("name");
                                    if (productNameString is null)
                                    {
                                        return default;
                                    }

                                    var productName = new ProductName(productNameString);

                                    var groupsJsonArray = productJsonObject.TryGetJsonArrayProperty("groups");
                                    if (groupsJsonArray is null)
                                    {
                                        return default;
                                    }

                                    if (groupsJsonArray.Any() is false)
                                    {
                                        return (productName, ImmutableList.Create<GroupName>());
                                    }

                                    // If groups are defined in configuration but none have a 'name' property, skip this resource
                                    var groupNames = groupsJsonArray.Choose(node => node as JsonObject)
                                                                    .Choose(groupJsonObject => groupJsonObject.TryGetStringProperty("name"))
                                                                    .Select(name => new GroupName(name))
                                                                    .ToImmutableList();
                                    return groupNames.Any() ? (productName, groupNames) : default;
                                });
    }

    private static async ValueTask Put(ProductName productName, IReadOnlyCollection<GroupName> groupNames, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var productUri = GetProductUri(productName, serviceUri);
        var productGroupsUri = new ProductGroupsUri(productUri);

        var existingGroupNames = await listRestResources(productGroupsUri.Uri, cancellationToken)
                                        .Select(groupJsonObject => groupJsonObject.GetStringProperty("name"))
                                        .Select(name => new GroupName(name))
                                        .ToListAsync(cancellationToken);

        var groupNamesToPut = groupNames.Except(existingGroupNames);
        var groupNamesToRemove = existingGroupNames.Except(groupNames);

        await groupNamesToRemove.ForEachParallel(async groupName =>
        {
            logger.LogInformation("Removing group {groupName} in product {productName}...", groupName, productName);
            await Delete(groupName, productUri, deleteRestResource, cancellationToken);
        }, cancellationToken);

        await groupNamesToPut.ForEachParallel(async groupName =>
        {
            logger.LogInformation("Putting group {groupName} in product {productName}...", groupName, productName);
            await Put(groupName, productUri, putRestResource, cancellationToken);
        }, cancellationToken);
    }

    private static ProductUri GetProductUri(ProductName productName, ServiceUri serviceUri)
    {
        var productsUri = new ProductsUri(serviceUri);
        return new ProductUri(productName, productsUri);
    }

    private static async ValueTask Delete(GroupName groupName, ProductUri productUri, DeleteRestResource deleteRestResource, CancellationToken cancellationToken)
    {
        var productGroupsUri = new ProductGroupsUri(productUri);
        var productGroupUri = new ProductGroupUri(groupName, productGroupsUri);

        await deleteRestResource(productGroupUri.Uri, cancellationToken);
    }

    private static async ValueTask Put(GroupName groupName, ProductUri productUri, PutRestResource putRestResource, CancellationToken cancellationToken)
    {
        var productGroupsUri = new ProductGroupsUri(productUri);
        var productGroupUri = new ProductGroupUri(groupName, productGroupsUri);

        await putRestResource(productGroupUri.Uri, new JsonObject(), cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }
}