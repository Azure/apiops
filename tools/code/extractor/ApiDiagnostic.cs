using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ApiDiagnostic
{
    public static async ValueTask ExportAll(ApiUri apiUri, ApiDirectory apiDirectory, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(apiUri, listRestResources, cancellationToken)
                .ForEachParallel(async diagnosticName => await Export(apiDirectory, apiUri, diagnosticName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<ApiDiagnosticName> List(ApiUri apiUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var diagnosticsUri = new ApiDiagnosticsUri(apiUri);
        var diagnosticJsonObjects = listRestResources(diagnosticsUri.Uri, cancellationToken);
        return diagnosticJsonObjects.Select(json => json.GetStringProperty("name"))
                                    .Select(name => new ApiDiagnosticName(name));
    }

    private static async ValueTask Export(ApiDirectory apiDirectory, ApiUri apiUri, ApiDiagnosticName apiDiagnosticName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiDiagnosticsDirectory = new ApiDiagnosticsDirectory(apiDirectory);
        var apiDiagnosticDirectory = new ApiDiagnosticDirectory(apiDiagnosticName, apiDiagnosticsDirectory);

        var apiDiagnosticsUri = new ApiDiagnosticsUri(apiUri);
        var apiDiagnosticUri = new ApiDiagnosticUri(apiDiagnosticName, apiDiagnosticsUri);

        await ExportInformationFile(apiDiagnosticDirectory, apiDiagnosticUri, apiDiagnosticName, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(ApiDiagnosticDirectory apiDiagnosticDirectory, ApiDiagnosticUri apiDiagnosticUri, ApiDiagnosticName apiDiagnosticName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiDiagnosticInformationFile = new ApiDiagnosticInformationFile(apiDiagnosticDirectory);

        var responseJson = await getRestResource(apiDiagnosticUri.Uri, cancellationToken);
        var apiDiagnosticModel = ApiDiagnosticModel.Deserialize(apiDiagnosticName, responseJson);
        var contentJson = apiDiagnosticModel.Serialize();

        logger.LogInformation("Writing API diagnostic information file {filePath}...", apiDiagnosticInformationFile.Path);
        await apiDiagnosticInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}
