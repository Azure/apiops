using common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ProductApi
{
    public static async ValueTask ExportAll(ProductDirectory productDirectory, ProductUri productUri, IEnumerable<string>? apiNamesToExport, ListRestResources listRestResources, ILogger logger, CancellationToken cancellationToken)
    {
        var productApisFile = new ProductApisFile(productDirectory);

        var productApis = await List(productUri, listRestResources, cancellationToken)
                                    // Filter out apis that should not be exported
                                    .Where(apiName => ShouldExport(apiName, apiNamesToExport))
                                    .Select(SerializeProductApi)
                                    .ToJsonArray(cancellationToken);

        if (productApis.Any())
        {
            logger.LogInformation("Writing product APIs file {filePath}...", productApisFile.Path);
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

    private static bool ShouldExport(ApiName apiName, IEnumerable<string>? apiNamesToExport)
    {
        return apiNamesToExport is null
               || apiNamesToExport.Any(apiNameToExport => apiNameToExport.Equals(apiName.ToString(), StringComparison.OrdinalIgnoreCase)
                                                          // Apis with revisions have the format 'apiName;revision'. We split by semicolon to get the name.
                                                          || apiNameToExport.Equals(apiName.ToString()
                                                                                           .Split(';')
                                                                                           .First(),
                                                                                    StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject SerializeProductApi(ApiName apiName)
    {
        return new JsonObject
        {
            ["name"] = apiName.ToString()
        };
    }
}
