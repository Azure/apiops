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

public delegate ValueTask ExtractLoggers(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(LoggerName Name, LoggerDto Dto)> ListLoggers(CancellationToken cancellationToken);
public delegate bool ShouldExtractLogger(LoggerName name);
public delegate ValueTask WriteLoggerArtifacts(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteLoggerInformationFile(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);

internal static class LoggerModule
{
    public static void ConfigureExtractLoggers(IHostApplicationBuilder builder)
    {
        ConfigureListLoggers(builder);
        ConfigureShouldExtractLogger(builder);
        ConfigureWriteLoggerArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractLoggers);
    }

    private static ExtractLoggers GetExtractLoggers(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListLoggers>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractLogger>();
        var writeArtifacts = provider.GetRequiredService<WriteLoggerArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractLoggers));

            logger.LogInformation("Extracting loggers...");

            await list(cancellationToken)
                    .Where(logger => shouldExtract(logger.Name))
                    .IterParallel(async logger => await writeArtifacts(logger.Name, logger.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListLoggers(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListLoggers);
    }

    private static ListLoggers GetListLoggers(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            LoggersUri.From(serviceUri)
                      .List(pipeline, cancellationToken);
    }

    public static void ConfigureShouldExtractLogger(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractLogger);
    }

    private static ShouldExtractLogger GetShouldExtractLogger(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<LoggerName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteLoggerArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteLoggerInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteLoggerArtifacts);
    }

    private static WriteLoggerArtifacts GetWriteLoggerArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteLoggerInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteLoggerInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteLoggerInformationFile);
    }

    private static WriteLoggerInformationFile GetWriteLoggerInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = LoggerInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing logger information file {LoggerInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}