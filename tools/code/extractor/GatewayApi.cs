using common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class GatewayApi
{
    public static async ValueTask ExportAll(GatewayDirectory gatewayDirectory, GatewayUri gatewayUri, IEnumerable<string>? apiNamesToExport, ListRestResources listRestResources, ILogger logger, CancellationToken cancellationToken)
    {
        var gatewayApisFile = new GatewayApisFile(gatewayDirectory);

        var gatewayApis = await List(gatewayUri, listRestResources, cancellationToken)
                                    // Filter out apis that should not be exported
                                    .Where(apiName => ShouldExport(apiName, apiNamesToExport))
                                    .Select(SerializeGatewayApi)
                                    .ToJsonArray(cancellationToken);

        if (gatewayApis.Any())
        {
            logger.LogInformation("Writing gateway APIs file {filePath}...", gatewayApisFile.Path);
            await gatewayApisFile.OverwriteWithJson(gatewayApis, cancellationToken);
        }
    }

    private static IAsyncEnumerable<ApiName> List(GatewayUri gatewayUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var apisUri = new GatewayApisUri(gatewayUri);
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

    private static JsonObject SerializeGatewayApi(ApiName apiName)
    {
        return new JsonObject
        {
            ["name"] = apiName.ToString()
        };
    }
}
