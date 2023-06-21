using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Logger
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, IEnumerable<string>? loggerNamesToExport, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                // Filter out apis that should not be exported
                .Where(loggerName => ShouldExport(loggerName, loggerNamesToExport))
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

    private static bool ShouldExport(LoggerName loggerName, IEnumerable<string>? loggerNamesToExport)
    {
        return loggerNamesToExport is null
               || loggerNamesToExport.Any(loggerNameToExport => loggerNameToExport.Equals(loggerName.ToString(), StringComparison.OrdinalIgnoreCase)
                                                          // Logger with revisions have the format 'loggerName;revision'. We split by semicolon to get the name.
                                                          || loggerNameToExport.Equals(loggerName.ToString()
                                                                                           .Split(';')
                                                                                           .First(),
                                                                                    StringComparison.OrdinalIgnoreCase));
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