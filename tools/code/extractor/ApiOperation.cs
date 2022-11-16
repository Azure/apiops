using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ApiOperation
{
    public static async ValueTask ExportAll(ApiUri apiUri, ApiDirectory apiDirectory, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(apiUri, listRestResources, cancellationToken)
                .ForEachParallel(async operationName => await Export(apiDirectory, apiUri, operationName, listRestResources, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ApiOperationName> List(ApiUri apiUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var operationsUri = new ApiOperationsUri(apiUri);
        var operationJsonObjects = listRestResources(operationsUri.Uri, cancellationToken);
        return operationJsonObjects.Select(json => json.GetStringProperty("name"))
                                   .Select(name => new ApiOperationName(name));
    }

    private static async ValueTask Export(ApiDirectory apiDirectory, ApiUri apiUri, ApiOperationName apiOperationName, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiOperationsDirectory = new ApiOperationsDirectory(apiDirectory);
        var apiOperationDirectory = new ApiOperationDirectory(apiOperationName, apiOperationsDirectory);

        var apiOperationsUri = new ApiOperationsUri(apiUri);
        var apiOperationUri = new ApiOperationUri(apiOperationName, apiOperationsUri);

        await ExportPolicies(apiOperationDirectory, apiOperationUri, listRestResources, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportPolicies(ApiOperationDirectory apiOperationDirectory, ApiOperationUri apiOperationUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await ApiOperationPolicy.ExportAll(apiOperationDirectory, apiOperationUri, listRestResources, getRestResource, logger, cancellationToken);
    }
}
