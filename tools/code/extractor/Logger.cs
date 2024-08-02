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

public delegate ValueTask ExtractLoggers(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(LoggerName Name, LoggerDto Dto)> ListLoggers(CancellationToken cancellationToken);
public delegate ValueTask WriteLoggerArtifacts(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteLoggerInformationFile(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);

internal static class LoggerModule
{
    public static void ConfigureExtractLoggers(IHostApplicationBuilder builder)
    {
        ConfigureListLoggers(builder);
        ConfigureWriteLoggerArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractLoggers);
    }

    private static ExtractLoggers GetExtractLoggers(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListLoggers>();
        var writeArtifacts = provider.GetRequiredService<WriteLoggerArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractLoggers));

            logger.LogInformation("Extracting loggers...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListLoggers(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListLoggers);
    }

    private static ListLoggers GetListLoggers(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<LoggerName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(LoggerName, LoggerDto)> listFromSet(IEnumerable<LoggerName> names, CancellationToken cancellationToken) =>
            names.Select(name => LoggerUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(LoggerName, LoggerDto)> listAll(CancellationToken cancellationToken)
        {
            var loggersUri = LoggersUri.From(serviceUri);
            return loggersUri.List(pipeline, cancellationToken);
        }
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