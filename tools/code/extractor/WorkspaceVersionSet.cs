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

public delegate ValueTask ExtractWorkspaceVersionSets(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceVersionSetName Name, WorkspaceVersionSetDto Dto)> ListWorkspaceVersionSets(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceVersionSetArtifacts(WorkspaceVersionSetName name, WorkspaceVersionSetDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceVersionSetInformationFile(WorkspaceVersionSetName name, WorkspaceVersionSetDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceVersionSetModule
{
    public static void ConfigureExtractWorkspaceVersionSets(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceVersionSets(builder);
        ConfigureWriteWorkspaceVersionSetArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceVersionSets);
    }

    private static ExtractWorkspaceVersionSets GetExtractWorkspaceVersionSets(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceVersionSets>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceVersionSetArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceVersionSets));

            logger.LogInformation("Extracting version sets in workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceVersionSets(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceVersionSets);
    }

    private static ListWorkspaceVersionSets GetListWorkspaceVersionSets(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
        {
            var workspaceVersionSetsUri = WorkspaceVersionSetsUri.From(workspaceName, serviceUri);
            return workspaceVersionSetsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceVersionSetArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceVersionSetInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceVersionSetArtifacts);
    }

    private static WriteWorkspaceVersionSetArtifacts GetWriteWorkspaceVersionSetArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceVersionSetInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceVersionSetInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceVersionSetInformationFile);
    }

    private static WriteWorkspaceVersionSetInformationFile GetWriteWorkspaceVersionSetInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceVersionSetInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace version set information file {WorkspaceVersionSetInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}