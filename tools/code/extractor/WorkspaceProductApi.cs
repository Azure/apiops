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

public delegate ValueTask ExtractWorkspaceProductApis(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceApiName Name, WorkspaceProductApiDto Dto)> ListWorkspaceProductApis(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductApiArtifacts(WorkspaceApiName name, WorkspaceProductApiDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductApiInformationFile(WorkspaceApiName name, WorkspaceProductApiDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductApiModule
{
    public static void ConfigureExtractWorkspaceProductApis(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceProductApis(builder);
        ConfigureWriteWorkspaceProductApiArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceProductApis);
    }

    private static ExtractWorkspaceProductApis GetExtractWorkspaceProductApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceProductApis>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceProductApiArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceProductApis));

            logger.LogInformation("Extracting APIs in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductName, workspaceName);

            await list(workspaceProductName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceProductName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceProductApis(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceProductApis);
    }

    private static ListWorkspaceProductApis GetListWorkspaceProductApis(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceProductName, workspaceName, cancellationToken) =>
        {
            var workspaceProductApisUri = WorkspaceProductApisUri.From(workspaceProductName, workspaceName, serviceUri);
            return workspaceProductApisUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceProductApiArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceProductApiInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductApiArtifacts);
    }

    private static WriteWorkspaceProductApiArtifacts GetWriteWorkspaceProductApiArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceProductApiInformationFile>();

        return async (name, dto, workspaceProductName, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceProductName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceProductApiInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductApiInformationFile);
    }

    private static WriteWorkspaceProductApiInformationFile GetWriteWorkspaceProductApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceProductName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductApiInformationFile.From(name, workspaceProductName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace product API information file {WorkspaceProductApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}