using common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal class Extractor : BackgroundService
{
    public record Parameters
    {
        public required ServiceDirectory ServiceDirectory { get; init; }
        public required ServiceUri ServiceUri { get; init; }
        public required DefaultApiSpecification DefaultApiSpecification { get; init; }
        public required ListRestResources ListRestResources { get; init; }
        public required GetRestResource GetRestResource { get; init; }
        public required DownloadResource DownloadResource { get; init; }
        public required ILogger Logger { get; init; }
        public required IHostApplicationLifetime ApplicationLifetime { get; init; }
        public IEnumerable<string>? ApiNamesToExport { get; init; }
    }

    private readonly Parameters parameters;

    public Extractor(Parameters parameters)
    {
        this.parameters = parameters;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var logger = parameters.Logger;

        try
        {
            logger.LogInformation("Beginning execution...");

            await ExportService(cancellationToken);

            logger.LogInformation("Execution complete.");
        }
        catch (OperationCanceledException)
        {
            // Don't throw if operation was canceled
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "");
            Environment.ExitCode = -1;
            throw;
        }
        finally
        {
            parameters.ApplicationLifetime.StopApplication();
        }
    }

    private async ValueTask ExportService(CancellationToken cancellationToken)
    {
        await Service.Export(parameters.ServiceDirectory,
                             parameters.ServiceUri,
                             parameters.DefaultApiSpecification,
                             parameters.ApiNamesToExport,
                             parameters.ListRestResources,
                             parameters.GetRestResource,
                             parameters.DownloadResource,
                             parameters.Logger,
                             cancellationToken);
    }
}