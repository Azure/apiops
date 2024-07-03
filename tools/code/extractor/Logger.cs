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

internal delegate ValueTask ExtractLoggers(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(LoggerName Name, LoggerDto Dto)> ListLoggers(CancellationToken cancellationToken);

internal delegate bool ShouldExtractLogger(LoggerName name);

file delegate ValueTask WriteLoggerArtifacts(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteLoggerInformationFile(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);

file sealed class ExtractLoggersHandler(ListLoggers list, ShouldExtractLogger shouldExtract, WriteLoggerArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(logger => shouldExtract(logger.Name))
                .IterParallel(async logger => await writeArtifacts(logger.Name, logger.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListLoggersHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(LoggerName, LoggerDto)> Handle(CancellationToken cancellationToken) =>
        LoggersUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractLoggerHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(LoggerName name)
    {
        var shouldExtract = shouldExtractFactory.Create<LoggerName>();
        return shouldExtract(name);
    }
}

file sealed class WriteLoggerArtifactsHandler(WriteLoggerInformationFile writeInformationFile)
{
    public async ValueTask Handle(LoggerName name, LoggerDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteLoggerInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(LoggerName name, LoggerDto dto, CancellationToken cancellationToken)
    {
        var informationFile = LoggerInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing logger information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class LoggerServices
{
    public static void ConfigureExtractLoggers(IServiceCollection services)
    {
        ConfigureListLoggers(services);
        ConfigureShouldExtractLogger(services);
        ConfigureWriteLoggerArtifacts(services);

        services.TryAddSingleton<ExtractLoggersHandler>();
        services.TryAddSingleton<ExtractLoggers>(provider => provider.GetRequiredService<ExtractLoggersHandler>().Handle);
    }

    private static void ConfigureListLoggers(IServiceCollection services)
    {
        services.TryAddSingleton<ListLoggersHandler>();
        services.TryAddSingleton<ListLoggers>(provider => provider.GetRequiredService<ListLoggersHandler>().Handle);
    }

    public static void ConfigureShouldExtractLogger(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractLoggerHandler>();
        services.TryAddSingleton<ShouldExtractLogger>(provider => provider.GetRequiredService<ShouldExtractLoggerHandler>().Handle);
    }

    private static void ConfigureWriteLoggerArtifacts(IServiceCollection services)
    {
        ConfigureWriteLoggerInformationFile(services);

        services.TryAddSingleton<WriteLoggerArtifactsHandler>();
        services.TryAddSingleton<WriteLoggerArtifacts>(provider => provider.GetRequiredService<WriteLoggerArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteLoggerInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteLoggerInformationFileHandler>();
        services.TryAddSingleton<WriteLoggerInformationFile>(provider => provider.GetRequiredService<WriteLoggerInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("LoggerExtractor");
}