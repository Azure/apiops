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

public delegate ValueTask ExtractWorkspaceApiPolicies(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceApiPolicyName Name, WorkspaceApiPolicyDto Dto)> ListWorkspaceApiPolicies(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiPolicyArtifacts(WorkspaceApiPolicyName name, WorkspaceApiPolicyDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiPolicyFile(WorkspaceApiPolicyName name, WorkspaceApiPolicyDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiPolicyModule
{
    public static void ConfigureExtractWorkspaceApiPolicies(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceApiPolicies(builder);
        ConfigureWriteWorkspaceApiPolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceApiPolicies);
    }

    private static ExtractWorkspaceApiPolicies GetExtractWorkspaceApiPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceApiPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceApiPolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceApiPolicies));

            logger.LogInformation("Extracting policies in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiName, workspaceName);

            await list(workspaceApiName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceApiName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceApiPolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceApiPolicies);
    }

    private static ListWorkspaceApiPolicies GetListWorkspaceApiPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceApiName, workspaceName, cancellationToken) =>
        {
            var workspaceApiPoliciesUri = WorkspaceApiPoliciesUri.From(workspaceApiName, workspaceName, serviceUri);
            return workspaceApiPoliciesUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceApiPolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceApiPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiPolicyArtifacts);
    }

    private static WriteWorkspaceApiPolicyArtifacts GetWriteWorkspaceApiPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteWorkspaceApiPolicyFile>();

        return async (name, dto, workspaceApiName, workspaceName, cancellationToken) =>
            await writePolicyFile(name, dto, workspaceApiName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceApiPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiPolicyFile);
    }

    private static WriteWorkspaceApiPolicyFile GetWriteWorkspaceApiPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var policyFile = WorkspaceApiPolicyFile.From(name, workspaceApiName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace API policy file {WorkspaceApiPolicyFile}...", policyFile);

            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}