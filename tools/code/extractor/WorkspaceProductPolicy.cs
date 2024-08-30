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

public delegate ValueTask ExtractWorkspaceProductPolicies(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceProductPolicyName Name, WorkspaceProductPolicyDto Dto)> ListWorkspaceProductPolicies(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductPolicyArtifacts(WorkspaceProductPolicyName name, WorkspaceProductPolicyDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceProductPolicyFile(WorkspaceProductPolicyName name, WorkspaceProductPolicyDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductPolicyModule
{
    public static void ConfigureExtractWorkspaceProductPolicies(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceProductPolicies(builder);
        ConfigureWriteWorkspaceProductPolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceProductPolicies);
    }

    private static ExtractWorkspaceProductPolicies GetExtractWorkspaceProductPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceProductPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceProductPolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceProductPolicies));

            logger.LogInformation("Extracting policies in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductName, workspaceName);

            await list(workspaceProductName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceProductName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceProductPolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceProductPolicies);
    }

    private static ListWorkspaceProductPolicies GetListWorkspaceProductPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceProductName, workspaceName, cancellationToken) =>
        {
            var workspaceProductPoliciesUri = WorkspaceProductPoliciesUri.From(workspaceProductName, workspaceName, serviceUri);
            return workspaceProductPoliciesUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceProductPolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceProductPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductPolicyArtifacts);
    }

    private static WriteWorkspaceProductPolicyArtifacts GetWriteWorkspaceProductPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteWorkspaceProductPolicyFile>();

        return async (name, dto, workspaceProductName, workspaceName, cancellationToken) =>
            await writePolicyFile(name, dto, workspaceProductName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceProductPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceProductPolicyFile);
    }

    private static WriteWorkspaceProductPolicyFile GetWriteWorkspaceProductPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceProductName, workspaceName, cancellationToken) =>
        {
            var policyFile = WorkspaceProductPolicyFile.From(name, workspaceProductName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace product policy file {WorkspaceProductPolicyFile}...", policyFile);

            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}