using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Diagnostic
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async diagnosticName => await Export(serviceDirectory, serviceUri, diagnosticName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<DiagnosticName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var diagnosticsUri = new DiagnosticsUri(serviceUri);
        var diagnosticJsonObjects = listRestResources(diagnosticsUri.Uri, cancellationToken);

        return diagnosticJsonObjects.Select(json => json.GetStringProperty("name"))
                                    .Select(name => new DiagnosticName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, DiagnosticName diagnosticName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var diagnosticsDirectory = new DiagnosticsDirectory(serviceDirectory);
        var diagnosticDirectory = new DiagnosticDirectory(diagnosticName, diagnosticsDirectory);

        var diagnosticsUri = new DiagnosticsUri(serviceUri);
        var diagnosticUri = new DiagnosticUri(diagnosticName, diagnosticsUri);

        await ExportInformationFile(diagnosticDirectory, diagnosticUri, diagnosticName, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(DiagnosticDirectory diagnosticDirectory, DiagnosticUri diagnosticUri, DiagnosticName diagnosticName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var diagnosticInformationFile = new DiagnosticInformationFile(diagnosticDirectory);

        var responseJson = await getRestResource(diagnosticUri.Uri, cancellationToken);
        var diagnosticModel = DiagnosticModel.Deserialize(diagnosticName, responseJson);
        var contentJson = diagnosticModel.Serialize();

        logger.LogInformation("Writing diagnostic information file {filePath}...", diagnosticInformationFile.Path);
        await diagnosticInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}