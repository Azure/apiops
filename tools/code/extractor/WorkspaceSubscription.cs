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

public delegate ValueTask ExtractWorkspaceSubscriptions(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(SubscriptionName Name, WorkspaceSubscriptionDto Dto)> ListWorkspaceSubscriptions(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceSubscriptionArtifacts(SubscriptionName name, WorkspaceSubscriptionDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceSubscriptionInformationFile(SubscriptionName name, WorkspaceSubscriptionDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceSubscriptionModule
{
    public static void ConfigureExtractWorkspaceSubscriptions(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceSubscriptions(builder);
        ConfigureWriteWorkspaceSubscriptionArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceSubscriptions);
    }

    private static ExtractWorkspaceSubscriptions GetExtractWorkspaceSubscriptions(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceSubscriptions>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceSubscriptionArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceSubscriptions));

            logger.LogInformation("Extracting subscriptions for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async subscription => await writeArtifacts(subscription.Name, subscription.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceSubscriptions(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceSubscriptions);
    }

    private static ListWorkspaceSubscriptions GetListWorkspaceSubscriptions(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspaceSubscriptionsUri.From(workspaceName, serviceUri)
                                     .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceSubscriptionArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceSubscriptionInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceSubscriptionArtifacts);
    }

    private static WriteWorkspaceSubscriptionArtifacts GetWriteWorkspaceSubscriptionArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceSubscriptionInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceSubscriptionInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceSubscriptionInformationFile);
    }

    private static WriteWorkspaceSubscriptionInformationFile GetWriteWorkspaceSubscriptionInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceSubscriptionInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace subscription information file {WorkspaceSubscriptionInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}