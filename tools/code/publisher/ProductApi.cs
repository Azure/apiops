using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using MoreLinq.Extensions;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Net.Http;

namespace publisher;

internal static class ProductApi
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }

    private static async ValueTask Put(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactProductApis(files, configurationJson, serviceDirectory)
                .ForEachParallel(async product => await Put(product.ProductName, product.ApiNames, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ProductName ProductName, ImmutableList<ApiName> ApiNames)> GetArtifactProductApis(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationProductApis(configurationJson);

        return GetProductApisFiles(files, serviceDirectory)
                .Where(file => File.Exists(file.Path.ToString()))
                .Select(file =>
                {
                    var productName = GetProductName(file);
                    var apiNames = file.ReadAsJsonArray()
                                    .Choose(node => node as JsonObject)
                                    .Choose(apiJsonObject => apiJsonObject.TryGetStringProperty("name"))
                                    .Select(name => new ApiName(name))
                                    .ToImmutableList();
                    return (ProductName: productName, ApiNames: apiNames);
                })
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.ProductName,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.ProductName, configurationArtifact.ApiNames));
    }

    private static IEnumerable<ProductApisFile> GetProductApisFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetApisFile(file, serviceDirectory));
    }

    private static ProductApisFile? TryGetApisFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ProductApisFile.Name) is false)
        {
            return null;
        }

        var productDirectory = Product.TryGetProductDirectory(file.Directory, serviceDirectory);

        return productDirectory is null
                ? null
                : new ProductApisFile(productDirectory);
    }

    private static ProductName GetProductName(ProductApisFile file)
    {
        return new(file.ProductDirectory.GetName());
    }

    private static IEnumerable<(ProductName ProductName, ImmutableList<ApiName> ApiNames)> GetConfigurationProductApis(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("products")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose<JsonObject, (ProductName ProductName, ImmutableList<ApiName> ApiNames)>(productJsonObject =>
                                {
                                    var productNameString = productJsonObject.TryGetStringProperty("name");
                                    if (productNameString is null)
                                    {
                                        return default;
                                    }

                                    var productName = new ProductName(productNameString);

                                    var apisJsonArray = productJsonObject.TryGetJsonArrayProperty("apis");
                                    if (apisJsonArray is null)
                                    {
                                        return default;
                                    }

                                    if (apisJsonArray.Any() is false)
                                    {
                                        return (productName, ImmutableList.Create<ApiName>());
                                    }

                                    // If APIs are defined in configuration but none have a 'name' property, skip this resource
                                    var apiNames = apisJsonArray.Choose(node => node as JsonObject)
                                                                .Choose(apiJsonObject => apiJsonObject.TryGetStringProperty("name"))
                                                                .Select(name => new ApiName(name))
                                                                .ToImmutableList();
                                    return apiNames.Any() ? (productName, apiNames) : default;
                                });
    }

    private static async ValueTask Put(ProductName productName, IReadOnlyCollection<ApiName> apiNames, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var productUri = GetProductUri(productName, serviceUri);
        var productApisUri = new ProductApisUri(productUri);
        
        var existingApiNames = await listRestResources(productApisUri.Uri, cancellationToken)
                                        .Select(apiJsonObject => apiJsonObject.GetStringProperty("name"))
                                        .Select(name => new ApiName(name))
                                        .ToListAsync(cancellationToken);

        var apiNamesToPut = apiNames.Except(existingApiNames);
        var apiNamesToRemove = existingApiNames.Except(apiNames);


        await apiNamesToRemove.ForEachParallel(async apiName =>
        {
            logger.LogInformation("Removing API {apiName} in product {productName}...", apiName, productName);
            await Delete(apiName, productUri, deleteRestResource, cancellationToken);
        }, cancellationToken);

        await apiNamesToPut.ForEachParallel(async apiName =>
        {
            logger.LogInformation("Putting API {apiName} in product {productName}...", apiName, productName);
            await Put(apiName, productUri, putRestResource,logger, cancellationToken);
        }, cancellationToken);
    }

    private static ProductUri GetProductUri(ProductName productName, ServiceUri serviceUri)
    {
        var productsUri = new ProductsUri(serviceUri);
        return new ProductUri(productName, productsUri);
    }

    private static async ValueTask Delete(ApiName apiName, ProductUri productUri, DeleteRestResource deleteRestResource, CancellationToken cancellationToken)
    {
        var productApisUri = new ProductApisUri(productUri);
        var productApiUri = new ProductApiUri(apiName, productApisUri);

        await deleteRestResource(productApiUri.Uri, cancellationToken);
    }

    private static async ValueTask Put(ApiName apiName, ProductUri productUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var productApisUri = new ProductApisUri(productUri);
        var productApiUri = new ProductApiUri(apiName, productApisUri);

        int retryCount = 0;
        bool retry = true;
        while (retry)
        {
            try
            {
                await putRestResource(productApiUri.Uri, new JsonObject(), cancellationToken);
                retry = false; // No exception occurred, so no need to retry
            }
            catch (HttpRequestException httpRequestException)
            {
                if (httpRequestException.Message.Contains("API cannot be added to more than one open products"))
                {
                    retryCount++;
                    if (retryCount <= 3)
                    {
                        // Log the retry attempt
                        logger.LogWarning("Retrying API put operation for {apiName}. Retry attempt: {retryCount}", apiName, retryCount);
                        // Wait for a certain duration before retrying
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), cancellationToken);
                    }
                    else
                    {
                        // Retry limit reached, throw the exception
                        throw;
                    }
                }
            }
        }
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }
}
