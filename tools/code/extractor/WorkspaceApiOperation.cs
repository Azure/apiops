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

public delegate ValueTask ExtractWorkspaceApiOperations(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceApiOperationName Name, WorkspaceApiOperationDto Dto)> ListWorkspaceApiOperations(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiOperationModule
{
    public static void ConfigureExtractWorkspaceApiOperations(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceApiOperations(builder);
        WorkspaceApiOperationPolicyModule.ConfigureExtractWorkspaceApiOperationPolicies(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceApiOperations);
    }

    private static ExtractWorkspaceApiOperations GetExtractWorkspaceApiOperations(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceApiOperations>();
        var extractPolicies = provider.GetRequiredService<ExtractWorkspaceApiOperationPolicies>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceApiOperations));

            logger.LogInformation("Extracting operations in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiName, workspaceName);

            await list(workspaceApiName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await extractPolicies(resource.Name, workspaceApiName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceApiOperations(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceApiOperations);
    }

    private static ListWorkspaceApiOperations GetListWorkspaceApiOperations(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceApiName, workspaceName, cancellationToken) =>
        {
            var workspaceApiOperationsUri = WorkspaceApiOperationsUri.From(workspaceApiName, workspaceName, serviceUri);
            return workspaceApiOperationsUri.List(pipeline, cancellationToken);
        };
    }
}