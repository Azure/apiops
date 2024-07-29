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

public delegate ValueTask ExtractWorkspaceGroups(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(GroupName Name, WorkspaceGroupDto Dto)> ListWorkspaceGroups(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceGroupArtifacts(GroupName name, WorkspaceGroupDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceGroupInformationFile(GroupName name, WorkspaceGroupDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceGroupModule
{
    public static void ConfigureExtractWorkspaceGroups(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceGroups(builder);
        ConfigureWriteWorkspaceGroupArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceGroups);
    }

    private static ExtractWorkspaceGroups GetExtractWorkspaceGroups(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceGroups>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceGroupArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceGroups));

            logger.LogInformation("Extracting groups for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async group => await writeArtifacts(group.Name, group.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceGroups(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceGroups);
    }

    private static ListWorkspaceGroups GetListWorkspaceGroups(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspaceGroupsUri.From(workspaceName, serviceUri)
                              .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceGroupArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceGroupInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceGroupArtifacts);
    }

    private static WriteWorkspaceGroupArtifacts GetWriteWorkspaceGroupArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceGroupInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceGroupInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceGroupInformationFile);
    }

    private static WriteWorkspaceGroupInformationFile GetWriteWorkspaceGroupInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceGroupInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace group information file {WorkspaceGroupInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}