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

public delegate ValueTask ExtractWorkspaceTags(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceTagName Name, WorkspaceTagDto Dto)> ListWorkspaceTags(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceTagArtifacts(WorkspaceTagName name, WorkspaceTagDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceTagInformationFile(WorkspaceTagName name, WorkspaceTagDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceTagModule
{
    public static void ConfigureExtractWorkspaceTags(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceTags(builder);
        ConfigureWriteWorkspaceTagArtifacts(builder);
        WorkspaceTagApiModule.ConfigureExtractWorkspaceTagApis(builder);
        WorkspaceTagProductModule.ConfigureExtractWorkspaceTagProducts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceTags);
    }

    private static ExtractWorkspaceTags GetExtractWorkspaceTags(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceTags>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceTagArtifacts>();
        var extractApis = provider.GetRequiredService<ExtractWorkspaceTagApis>();
        var extractProducts = provider.GetRequiredService<ExtractWorkspaceTagProducts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceTags));

            logger.LogInformation("Extracting tags in workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceName, cancellationToken);
                        await extractApis(resource.Name, workspaceName, cancellationToken);
                        await extractProducts(resource.Name, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceTags(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceTags);
    }

    private static ListWorkspaceTags GetListWorkspaceTags(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
        {
            var workspaceTagsUri = WorkspaceTagsUri.From(workspaceName, serviceUri);
            return workspaceTagsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceTagArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceTagInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceTagArtifacts);
    }

    private static WriteWorkspaceTagArtifacts GetWriteWorkspaceTagArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceTagInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceTagInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceTagInformationFile);
    }

    private static WriteWorkspaceTagInformationFile GetWriteWorkspaceTagInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceTagInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace tag information file {WorkspaceTagInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}