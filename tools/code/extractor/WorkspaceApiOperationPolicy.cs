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

public delegate ValueTask ExtractWorkspaceApiOperationPolicies(WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceApiOperationPolicyName Name, WorkspaceApiOperationPolicyDto Dto)> ListWorkspaceApiOperationPolicies(WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiOperationPolicyArtifacts(WorkspaceApiOperationPolicyName name, WorkspaceApiOperationPolicyDto dto, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiOperationPolicyFile(WorkspaceApiOperationPolicyName name, WorkspaceApiOperationPolicyDto dto, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiOperationPolicyModule
{
    public static void ConfigureExtractWorkspaceApiOperationPolicies(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceApiOperationPolicies(builder);
        ConfigureWriteWorkspaceApiOperationPolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceApiOperationPolicies);
    }

    private static ExtractWorkspaceApiOperationPolicies GetExtractWorkspaceApiOperationPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceApiOperationPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceApiOperationPolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceApiOperationPolicies));

            logger.LogInformation("Extracting policies in operation {WorkspaceApiOperationName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiOperationName, workspaceApiName, workspaceName);

            await list(workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceApiOperationPolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceApiOperationPolicies);
    }

    private static ListWorkspaceApiOperationPolicies GetListWorkspaceApiOperationPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var workspaceApiOperationPoliciesUri = WorkspaceApiOperationPoliciesUri.From(workspaceApiOperationName, workspaceApiName, workspaceName, serviceUri);
            return workspaceApiOperationPoliciesUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceApiOperationPolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceApiOperationPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiOperationPolicyArtifacts);
    }

    private static WriteWorkspaceApiOperationPolicyArtifacts GetWriteWorkspaceApiOperationPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteWorkspaceApiOperationPolicyFile>();

        return async (name, dto, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
            await writePolicyFile(name, dto, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceApiOperationPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiOperationPolicyFile);
    }

    private static WriteWorkspaceApiOperationPolicyFile GetWriteWorkspaceApiOperationPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var policyFile = WorkspaceApiOperationPolicyFile.From(name, workspaceApiOperationName, workspaceApiName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace API operation policy file {WorkspaceApiOperationPolicyFile}...", policyFile);

            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}