using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractDiagnostics(CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<(DiagnosticName Name, DiagnosticDto Dto)> ListDiagnostics(CancellationToken cancellationToken);

internal delegate bool ShouldExtractDiagnostic(DiagnosticName name, DiagnosticDto dto);

internal delegate ValueTask WriteDiagnosticArtifacts(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

internal delegate ValueTask WriteDiagnosticInformationFile(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

internal static class DiagnosticServices
{
    public static void ConfigureExtractDiagnostics(IServiceCollection services)
    {
        ConfigureListDiagnostics(services);
        ConfigureShouldExtractDiagnostic(services);
        ConfigureWriteDiagnosticArtifacts(services);

        services.TryAddSingleton(ExtractDiagnostics);
    }

    private static ExtractDiagnostics ExtractDiagnostics(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListDiagnostics>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractDiagnostic>();
        var writeArtifacts = provider.GetRequiredService<WriteDiagnosticArtifacts>();

        return async cancellationToken =>
            await list(cancellationToken)
                    .Where(diagnostic => shouldExtract(diagnostic.Name, diagnostic.Dto))
                    .IterParallel(async diagnostic => await writeArtifacts(diagnostic.Name, diagnostic.Dto, cancellationToken),
                                  cancellationToken);
    }

    private static void ConfigureListDiagnostics(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListDiagnostics);
    }

    private static ListDiagnostics ListDiagnostics(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            DiagnosticsUri.From(serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractDiagnostic(IServiceCollection services)
    {
        CommonServices.ConfigureShouldExtractFactory(services);
        LoggerServices.ConfigureShouldExtractLogger(services);

        services.TryAddSingleton(ShouldExtractDiagnostic);
    }

    private static ShouldExtractDiagnostic ShouldExtractDiagnostic(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();
        var shouldExtractLogger = provider.GetRequiredService<ShouldExtractLogger>();

        var shouldExtractDiagnosticName = shouldExtractFactory.Create<DiagnosticName>();

        return (name, dto) =>
            shouldExtractDiagnosticName(name)
            && DiagnosticModule.TryGetLoggerName(dto)
                               .Map(shouldExtractLogger.Invoke)
                               .IfNone(true);
    }

    private static void ConfigureWriteDiagnosticArtifacts(IServiceCollection services)
    {
        ConfigureWriteDiagnosticInformationFile(services);

        services.TryAddSingleton(WriteDiagnosticArtifacts);
    }

    private static WriteDiagnosticArtifacts WriteDiagnosticArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteDiagnosticInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteDiagnosticInformationFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteDiagnosticInformationFile);
    }

    private static WriteDiagnosticInformationFile WriteDiagnosticInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing diagnostic information file {DiagnosticInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("DiagnosticExtractor");
}