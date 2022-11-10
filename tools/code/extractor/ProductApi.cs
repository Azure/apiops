using common;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ProductApi
{
    public static async ValueTask ExportAll(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var productApisFile = new ProductApisFile(productDirectory);

        var productApis = await List(productUri, listRestResources, cancellationToken)
                                    .Select(SerializeProductApi)
                                    .ToJsonArray(cancellationToken);

        if (productApis.Any())
        {
            await productApisFile.OverwriteWithJson(productApis, cancellationToken);
        }
    }

    private static IAsyncEnumerable<ApiName> List(ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var apisUri = new ProductApisUri(productUri);
        var apiJsonObjects = listRestResources(apisUri.Uri, cancellationToken);
        return apiJsonObjects.Select(json => json.GetStringProperty("name"))
                             .Select(name => new ApiName(name));
    }

    private static JsonObject SerializeProductApi(ApiName apiName)
    {
        return new JsonObject
        {
            ["name"] = apiName.ToString()
        };
    }
}
