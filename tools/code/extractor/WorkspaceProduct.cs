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

public delegate ValueTask ExtractWorkspaceProducts(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ProductName Name, WorkspaceProductDto Dto)> ListWorkspaceProducts(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductArtifacts(ProductName name, WorkspaceProductDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductInformationFile(ProductName name, WorkspaceProductDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductModule
{
    public static void ConfigureExtractWorkspaceProducts(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceProducts(builder);
        ConfigureWriteWorkspaceProductArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceProducts);
    }

    private static ExtractWorkspaceProducts GetExtractWorkspaceProducts(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceProducts>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceProductArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceProducts));

            logger.LogInformation("Extracting products for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async product => await writeArtifacts(product.Name, product.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceProducts(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceProducts);
    }

    private static ListWorkspaceProducts GetListWorkspaceProducts(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspaceProductsUri.From(workspaceName, serviceUri)
                                .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceProductArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceProductInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductArtifacts);
    }

    private static WriteWorkspaceProductArtifacts GetWriteWorkspaceProductArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceProductInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceProductInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductInformationFile);
    }

    private static WriteWorkspaceProductInformationFile GetWriteWorkspaceProductInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace product information file {WorkspaceProductInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}