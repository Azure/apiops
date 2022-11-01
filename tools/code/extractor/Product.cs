using common;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Product
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async productName => await Export(serviceDirectory,
                                                                   serviceUri,
                                                                   productName,
                                                                   listRestResources,
                                                                   getRestResource,
                                                                   cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ProductName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var productsUri = new ProductsUri(serviceUri);
        var productJsonObjects = listRestResources(productsUri.Uri, cancellationToken);
        return productJsonObjects.Select(json => json.GetStringProperty("name"))
                                 .Select(name => new ProductName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ProductName productName, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var productsDirectory = new ProductsDirectory(serviceDirectory);
        var productDirectory = new ProductDirectory(productName, productsDirectory);

        var productsUri = new ProductsUri(serviceUri);
        var productUri = new ProductUri(productName, productsUri);

        await ExportInformationFile(productDirectory, productUri, productName, getRestResource, cancellationToken);
        await ExportPolicies(productDirectory, productUri, listRestResources, getRestResource, cancellationToken);
        await ExportApis(productDirectory, productUri, listRestResources, cancellationToken);
        await ExportGroups(productDirectory, productUri, listRestResources, cancellationToken);
        await ExportTags(productDirectory, productUri, listRestResources, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(ProductDirectory productDirectory, ProductUri productUri, ProductName productName, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        var productInformationFile = new ProductInformationFile(productDirectory);

        var responseJson = await getRestResource(productUri.Uri, cancellationToken);
        var productModel = ProductModel.Deserialize(productName, responseJson);
        var contentJson = productModel.Serialize();

        await productInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }

    private static async ValueTask ExportPolicies(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, GetRestResource getRestResource, CancellationToken cancellationToken)
    {
        await ProductPolicy.ExportAll(productDirectory, productUri, listRestResources, getRestResource, cancellationToken);
    }

    private static async ValueTask ExportApis(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        await ProductApi.ExportAll(productDirectory, productUri, listRestResources, cancellationToken);
    }

    private static async ValueTask ExportGroups(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        await ProductGroup.ExportAll(productDirectory, productUri, listRestResources, cancellationToken);
    }

    private static async ValueTask ExportTags(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        await ProductTag.ExportAll(productDirectory, productUri, listRestResources, cancellationToken);
    }
}