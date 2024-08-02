using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractDiagnostics(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(DiagnosticName Name, DiagnosticDto Dto)> ListDiagnostics(CancellationToken cancellationToken);
public delegate ValueTask WriteDiagnosticArtifacts(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteDiagnosticInformationFile(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

internal static class DiagnosticModule
{
    public static void ConfigureExtractDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureListDiagnostics(builder);
        ConfigureWriteDiagnosticArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractDiagnostics);
    }

    private static ExtractDiagnostics GetExtractDiagnostics(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListDiagnostics>();
        var writeArtifacts = provider.GetRequiredService<WriteDiagnosticArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractDiagnostics));

            logger.LogInformation("Extracting diagnostics...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListDiagnostics);
    }

    private static ListDiagnostics GetListDiagnostics(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<DiagnosticName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(DiagnosticName, DiagnosticDto)> listFromSet(IEnumerable<DiagnosticName> names, CancellationToken cancellationToken) =>
            names.Select(name => DiagnosticUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(DiagnosticName, DiagnosticDto)> listAll(CancellationToken cancellationToken)
        {
            var diagnosticsUri = DiagnosticsUri.From(serviceUri);
            return diagnosticsUri.List(pipeline, cancellationToken);
        }
    }

    private static void ConfigureWriteDiagnosticArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteDiagnosticInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteDiagnosticArtifacts);
    }

    private static WriteDiagnosticArtifacts GetWriteDiagnosticArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteDiagnosticInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteDiagnosticInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteDiagnosticInformationFile);
    }

    private static WriteDiagnosticInformationFile GetWriteDiagnosticInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing diagnostic information file {DiagnosticInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}