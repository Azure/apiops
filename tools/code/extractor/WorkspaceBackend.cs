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

public delegate ValueTask ExtractWorkspaceBackends(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(BackendName Name, WorkspaceBackendDto Dto)> ListWorkspaceBackends(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceBackendArtifacts(BackendName name, WorkspaceBackendDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceBackendInformationFile(BackendName name, WorkspaceBackendDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceBackendModule
{
    public static void ConfigureExtractWorkspaceBackends(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceBackends(builder);
        ConfigureWriteWorkspaceBackendArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceBackends);
    }

    private static ExtractWorkspaceBackends GetExtractWorkspaceBackends(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceBackends>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceBackendArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceBackends));

            logger.LogInformation("Extracting backends for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceBackends(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceBackends);
    }

    private static ListWorkspaceBackends GetListWorkspaceBackends(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
        {
            var workspaceBackendsUri = WorkspaceBackendsUri.From(workspaceName, serviceUri);
            return workspaceBackendsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceBackendArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceBackendInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceBackendArtifacts);
    }

    private static WriteWorkspaceBackendArtifacts GetWriteWorkspaceBackendArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceBackendInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceBackendInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceBackendInformationFile);
    }

    private static WriteWorkspaceBackendInformationFile GetWriteWorkspaceBackendInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceBackendInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace backend information file {WorkspaceBackendInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}