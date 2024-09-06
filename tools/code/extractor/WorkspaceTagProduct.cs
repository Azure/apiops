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

public delegate ValueTask ExtractWorkspaceTagProducts(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceProductName Name, WorkspaceTagProductDto Dto)> ListWorkspaceTagProducts(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceTagProductArtifacts(WorkspaceProductName name, WorkspaceTagProductDto dto, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceTagProductInformationFile(WorkspaceProductName name, WorkspaceTagProductDto dto, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceTagProductModule
{
    public static void ConfigureExtractWorkspaceTagProducts(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceTagProducts(builder);
        ConfigureWriteWorkspaceTagProductArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceTagProducts);
    }

    private static ExtractWorkspaceTagProducts GetExtractWorkspaceTagProducts(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceTagProducts>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceTagProductArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceTagName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceTagProducts));

            logger.LogInformation("Extracting products in tag {WorkspaceTagName} in workspace {WorkspaceName}...", workspaceTagName, workspaceName);

            await list(workspaceTagName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceTagName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceTagProducts(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceTagProducts);
    }

    private static ListWorkspaceTagProducts GetListWorkspaceTagProducts(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceTagName, workspaceName, cancellationToken) =>
        {
            var workspaceTagProductsUri = WorkspaceTagProductsUri.From(workspaceTagName, workspaceName, serviceUri);
            return workspaceTagProductsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceTagProductArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceTagProductInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceTagProductArtifacts);
    }

    private static WriteWorkspaceTagProductArtifacts GetWriteWorkspaceTagProductArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceTagProductInformationFile>();

        return async (name, dto, workspaceTagName, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceTagName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceTagProductInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceTagProductInformationFile);
    }

    private static WriteWorkspaceTagProductInformationFile GetWriteWorkspaceTagProductInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceTagName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceTagProductInformationFile.From(name, workspaceTagName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace tag product information file {WorkspaceTagProductInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}