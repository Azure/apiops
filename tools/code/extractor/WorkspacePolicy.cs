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

public delegate ValueTask ExtractWorkspacePolicies(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspacePolicyName Name, WorkspacePolicyDto Dto)> ListWorkspacePolicies(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspacePolicyArtifacts(WorkspacePolicyName name, WorkspacePolicyDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspacePolicyFile(WorkspacePolicyName name, WorkspacePolicyDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspacePolicyModule
{
    public static void ConfigureExtractWorkspacePolicies(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspacePolicies(builder);
        ConfigureWriteWorkspacePolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspacePolicies);
    }

    private static ExtractWorkspacePolicies GetExtractWorkspacePolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspacePolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspacePolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspacePolicies));

            logger.LogInformation("Extracting policies for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async policy => await writeArtifacts(policy.Name, policy.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspacePolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspacePolicies);
    }

    private static ListWorkspacePolicies GetListWorkspacePolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspacePoliciesUri.From(workspaceName, serviceUri)
                                .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspacePolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspacePolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspacePolicyArtifacts);
    }

    private static WriteWorkspacePolicyArtifacts GetWriteWorkspacePolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteWorkspacePolicyFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
            await writePolicyFile(name, dto, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspacePolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspacePolicyFile);
    }

    private static WriteWorkspacePolicyFile GetWriteWorkspacePolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var policyFile = WorkspacePolicyFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace policy file {PolicyFile}", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}