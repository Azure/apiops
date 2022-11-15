using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Logger
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async loggerName => await Export(serviceDirectory, serviceUri, loggerName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<LoggerName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var loggersUri = new LoggersUri(serviceUri);
        var loggerJsonObjects = listRestResources(loggersUri.Uri, cancellationToken);
        return loggerJsonObjects.Select(json => json.GetStringProperty("name"))
                                .Select(name => new LoggerName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, LoggerName loggerName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var loggersDirectory = new LoggersDirectory(serviceDirectory);
        var loggerDirectory = new LoggerDirectory(loggerName, loggersDirectory);

        var loggersUri = new LoggersUri(serviceUri);
        var loggerUri = new LoggerUri(loggerName, loggersUri);

        await ExportInformationFile(loggerDirectory, loggerUri, loggerName, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(LoggerDirectory loggerDirectory, LoggerUri loggerUri, LoggerName loggerName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var loggerInformationFile = new LoggerInformationFile(loggerDirectory);

        var responseJson = await getRestResource(loggerUri.Uri, cancellationToken);
        var loggerModel = LoggerModel.Deserialize(loggerName, responseJson);
        var contentJson = loggerModel.Serialize();

        logger.LogInformation("Writing logger information file {filePath}...", loggerInformationFile.Path);
        await loggerInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}