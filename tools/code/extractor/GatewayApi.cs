using common;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class GatewayApi
{
    public static async ValueTask ExportAll(GatewayDirectory gatewayDirectory, GatewayUri gatewayUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var gatewayApisFile = new GatewayApisFile(gatewayDirectory);

        var gatewayApis = await List(gatewayUri, listRestResources, cancellationToken)
                                    .Select(SerializeGatewayApi)
                                    .ToJsonArray(cancellationToken);

        if (gatewayApis.Any())
        {
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

    private static JsonObject SerializeGatewayApi(ApiName apiName)
    {
        return new JsonObject
        {
            ["name"] = apiName.ToString()
        };
    }
}
