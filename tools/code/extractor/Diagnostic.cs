using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractDiagnostics(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(DiagnosticName Name, DiagnosticDto Dto)> ListDiagnostics(CancellationToken cancellationToken);

file delegate bool ShouldExtractDiagnostic(DiagnosticName name, DiagnosticDto dto);

file delegate ValueTask WriteDiagnosticArtifacts(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteDiagnosticInformationFile(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

file sealed class ExtractDiagnosticsHandler(ListDiagnostics list, ShouldExtractDiagnostic shouldExtract, WriteDiagnosticArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(diagnostic => shouldExtract(diagnostic.Name, diagnostic.Dto))
                .IterParallel(async diagnostic => await writeArtifacts(diagnostic.Name, diagnostic.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListDiagnosticsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(DiagnosticName, DiagnosticDto)> Handle(CancellationToken cancellationToken) =>
        DiagnosticsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractDiagnosticHandler(ShouldExtractFactory shouldExtractFactory, ShouldExtractLogger shouldExtractLogger)
{
    public bool Handle(DiagnosticName name, DiagnosticDto dto) =>
        // Check name from configuration override
        shouldExtractFactory.Create<DiagnosticName>().Invoke(name)
        // Don't extract diagnostic if its logger should not be extracted
        && DiagnosticModule.TryGetLoggerName(dto)
                           .Map(shouldExtractLogger.Invoke)
                           .IfNone(true);
}

file sealed class WriteDiagnosticArtifactsHandler(WriteDiagnosticInformationFile writeInformationFile)
{
    public async ValueTask Handle(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteDiagnosticInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken)
    {
        var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing diagnostic information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class DiagnosticServices
{
    public static void ConfigureExtractDiagnostics(IServiceCollection services)
    {
        ConfigureListDiagnostics(services);
        ConfigureShouldExtractDiagnostic(services);
        ConfigureWriteDiagnosticArtifacts(services);

        services.TryAddSingleton<ExtractDiagnosticsHandler>();
        services.TryAddSingleton<ExtractDiagnostics>(provider => provider.GetRequiredService<ExtractDiagnosticsHandler>().Handle);
    }

    private static void ConfigureListDiagnostics(IServiceCollection services)
    {
        services.TryAddSingleton<ListDiagnosticsHandler>();
        services.TryAddSingleton<ListDiagnostics>(provider => provider.GetRequiredService<ListDiagnosticsHandler>().Handle);
    }

    private static void ConfigureShouldExtractDiagnostic(IServiceCollection services)
    {
        LoggerServices.ConfigureShouldExtractLogger(services);

        services.TryAddSingleton<ShouldExtractDiagnosticHandler>();
        services.TryAddSingleton<ShouldExtractDiagnostic>(provider => provider.GetRequiredService<ShouldExtractDiagnosticHandler>().Handle);
    }

    private static void ConfigureWriteDiagnosticArtifacts(IServiceCollection services)
    {
        ConfigureWriteDiagnosticInformationFile(services);

        services.TryAddSingleton<WriteDiagnosticArtifactsHandler>();
        services.TryAddSingleton<WriteDiagnosticArtifacts>(provider => provider.GetRequiredService<WriteDiagnosticArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteDiagnosticInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteDiagnosticInformationFileHandler>();
        services.TryAddSingleton<WriteDiagnosticInformationFile>(provider => provider.GetRequiredService<WriteDiagnosticInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("DiagnosticExtractor");
}