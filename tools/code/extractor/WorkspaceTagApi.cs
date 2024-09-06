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

public delegate ValueTask ExtractWorkspaceTagApis(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceApiName Name, WorkspaceTagApiDto Dto)> ListWorkspaceTagApis(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceTagApiArtifacts(WorkspaceApiName name, WorkspaceTagApiDto dto, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceTagApiInformationFile(WorkspaceApiName name, WorkspaceTagApiDto dto, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceTagApiModule
{
    public static void ConfigureExtractWorkspaceTagApis(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceTagApis(builder);
        ConfigureWriteWorkspaceTagApiArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceTagApis);
    }

    private static ExtractWorkspaceTagApis GetExtractWorkspaceTagApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceTagApis>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceTagApiArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceTagName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceTagApis));

            logger.LogInformation("Extracting APIs in tag {WorkspaceTagName} in workspace {WorkspaceName}...", workspaceTagName, workspaceName);

            await list(workspaceTagName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceTagName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceTagApis(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceTagApis);
    }

    private static ListWorkspaceTagApis GetListWorkspaceTagApis(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceTagName, workspaceName, cancellationToken) =>
        {
            var workspaceTagApisUri = WorkspaceTagApisUri.From(workspaceTagName, workspaceName, serviceUri);
            return workspaceTagApisUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceTagApiArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceTagApiInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceTagApiArtifacts);
    }

    private static WriteWorkspaceTagApiArtifacts GetWriteWorkspaceTagApiArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceTagApiInformationFile>();

        return async (name, dto, workspaceTagName, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceTagName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceTagApiInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceTagApiInformationFile);
    }

    private static WriteWorkspaceTagApiInformationFile GetWriteWorkspaceTagApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceTagName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceTagApiInformationFile.From(name, workspaceTagName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace tag API information file {WorkspaceTagApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}