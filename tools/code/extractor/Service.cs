using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Service
{
    public static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, DefaultApiSpecification defaultSpecification, IEnumerable<string>? apiNamesToExport, ListRestResources listRestResources, GetRestResource getRestResource, DownloadResource downloadResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Exporting named values...");
        await NamedValue.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting tags...");
        await Tag.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting version sets...");
        await ApiVersionSet.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting loggers...");
        await Logger.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting diagnostics...");
        await Diagnostic.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting backends...");
        await Backend.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting products...");
        await Product.ExportAll(serviceDirectory, serviceUri, apiNamesToExport, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting gateways...");
        await Gateway.ExportAll(serviceDirectory, serviceUri, apiNamesToExport, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting policy fragments...");
        await PolicyFragment.ExportAll(serviceDirectory, serviceUri, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting service policies...");
        await ServicePolicy.ExportAll(serviceUri, serviceDirectory, listRestResources, getRestResource, logger, cancellationToken);

        logger.LogInformation("Exporting apis...");
        await Api.ExportAll(serviceDirectory, serviceUri, defaultSpecification, apiNamesToExport, listRestResources, getRestResource, downloadResource, logger, cancellationToken);
    }
}