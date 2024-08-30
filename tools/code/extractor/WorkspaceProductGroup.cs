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

public delegate ValueTask ExtractWorkspaceProductGroups(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceGroupName Name, WorkspaceProductGroupDto Dto)> ListWorkspaceProductGroups(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductGroupArtifacts(WorkspaceGroupName name, WorkspaceProductGroupDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductGroupInformationFile(WorkspaceGroupName name, WorkspaceProductGroupDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductGroupModule
{
    public static void ConfigureExtractWorkspaceProductGroups(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceProductGroups(builder);
        ConfigureWriteWorkspaceProductGroupArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceProductGroups);
    }

    private static ExtractWorkspaceProductGroups GetExtractWorkspaceProductGroups(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceProductGroups>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceProductGroupArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceProductGroups));

            logger.LogInformation("Extracting groups in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductName, workspaceName);

            await list(workspaceProductName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceProductName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceProductGroups(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceProductGroups);
    }

    private static ListWorkspaceProductGroups GetListWorkspaceProductGroups(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceProductName, workspaceName, cancellationToken) =>
        {
            var workspaceProductGroupsUri = WorkspaceProductGroupsUri.From(workspaceProductName, workspaceName, serviceUri);
            return workspaceProductGroupsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceProductGroupArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceProductGroupInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductGroupArtifacts);
    }

    private static WriteWorkspaceProductGroupArtifacts GetWriteWorkspaceProductGroupArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceProductGroupInformationFile>();

        return async (name, dto, workspaceProductName, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceProductName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceProductGroupInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductGroupInformationFile);
    }

    private static WriteWorkspaceProductGroupInformationFile GetWriteWorkspaceProductGroupInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceProductName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductGroupInformationFile.From(name, workspaceProductName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace product group information file {WorkspaceProductGroupInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}