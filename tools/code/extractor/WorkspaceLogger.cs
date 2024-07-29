using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractWorkspaceLoggers(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(LoggerName Name, WorkspaceLoggerDto Dto)> ListWorkspaceLoggers(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceLoggerArtifacts(LoggerName name, WorkspaceLoggerDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceLoggerInformationFile(LoggerName name, WorkspaceLoggerDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceLoggerModule
{
    public static void ConfigureExtractWorkspaceLoggers(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceLoggers(builder);
        ConfigureWriteWorkspaceLoggerArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceLoggers);
    }

    private static ExtractWorkspaceLoggers GetExtractWorkspaceLoggers(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceLoggers>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceLoggerArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceLoggers));

            logger.LogInformation("Extracting loggers for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async logger => await writeArtifacts(logger.Name, logger.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceLoggers(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceLoggers);
    }

    private static ListWorkspaceLoggers GetListWorkspaceLoggers(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspaceLoggersUri.From(workspaceName, serviceUri)
                               .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceLoggerArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceLoggerInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceLoggerArtifacts);
    }

    private static WriteWorkspaceLoggerArtifacts GetWriteWorkspaceLoggerArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceLoggerInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceLoggerInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceLoggerInformationFile);
    }

    private static WriteWorkspaceLoggerInformationFile GetWriteWorkspaceLoggerInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceLoggerInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace logger information file {WorkspaceLoggerInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}