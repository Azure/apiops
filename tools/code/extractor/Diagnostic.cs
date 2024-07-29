using Azure.Core.Pipeline;
using common;
using LanguageExt;
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
public delegate bool ShouldExtractDiagnostic(DiagnosticName name, DiagnosticDto dto);
public delegate ValueTask WriteDiagnosticArtifacts(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteDiagnosticInformationFile(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

internal static class DiagnosticModule
{
    public static void ConfigureExtractDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureListDiagnostics(builder);
        ConfigureShouldExtractDiagnostic(builder);
        ConfigureWriteDiagnosticArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractDiagnostics);
    }

    private static ExtractDiagnostics GetExtractDiagnostics(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListDiagnostics>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractDiagnostic>();
        var writeArtifacts = provider.GetRequiredService<WriteDiagnosticArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractDiagnostics));

            logger.LogInformation("Extracting diagnostics...");

            await list(cancellationToken)
                    .Where(diagnostic => shouldExtract(diagnostic.Name, diagnostic.Dto))
                    .IterParallel(async diagnostic => await writeArtifacts(diagnostic.Name, diagnostic.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListDiagnostics(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListDiagnostics);
    }

    private static ListDiagnostics GetListDiagnostics(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            DiagnosticsUri.From(serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractDiagnostic(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);
        LoggerModule.ConfigureShouldExtractLogger(builder);

        builder.Services.TryAddSingleton(GetShouldExtractDiagnostic);
    }

    private static ShouldExtractDiagnostic GetShouldExtractDiagnostic(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();
        var shouldExtractLogger = provider.GetRequiredService<ShouldExtractLogger>();

        var shouldExtractDiagnosticName = shouldExtractFactory.Create<DiagnosticName>();

        return (name, dto) =>
            shouldExtractDiagnosticName(name)
            && common.DiagnosticModule.TryGetLoggerName(dto)
                                      .Map(shouldExtractLogger.Invoke)
                                      .IfNone(true);
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