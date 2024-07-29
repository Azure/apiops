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

public delegate ValueTask ExtractWorkspacePolicyFragments(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(PolicyFragmentName Name, WorkspacePolicyFragmentDto Dto)> ListWorkspacePolicyFragments(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspacePolicyFragmentArtifacts(PolicyFragmentName name, WorkspacePolicyFragmentDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspacePolicyFragmentInformationFile(PolicyFragmentName name, WorkspacePolicyFragmentDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspacePolicyFragmentPolicyFile(PolicyFragmentName name, WorkspacePolicyFragmentDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspacePolicyFragmentModule
{
    public static void ConfigureExtractWorkspacePolicyFragments(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspacePolicyFragments(builder);
        ConfigureWriteWorkspacePolicyFragmentArtifacts(builder);
        ConfigureWriteWorkspacePolicyFragmentArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspacePolicyFragments);
    }

    private static ExtractWorkspacePolicyFragments GetExtractWorkspacePolicyFragments(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspacePolicyFragments>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspacePolicyFragmentArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspacePolicyFragments));

            logger.LogInformation("Extracting policy fragments for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async policyFragment => await writeArtifacts(policyFragment.Name, policyFragment.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspacePolicyFragments(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspacePolicyFragments);
    }

    private static ListWorkspacePolicyFragments GetListWorkspacePolicyFragments(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspacePolicyFragmentsUri.From(workspaceName, serviceUri)
                                       .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspacePolicyFragmentArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspacePolicyFragmentInformationFile(builder);
        ConfigureWriteWorkspacePolicyFragmentPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspacePolicyFragmentArtifacts);
    }

    private static WriteWorkspacePolicyFragmentArtifacts GetWriteWorkspacePolicyFragmentArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspacePolicyFragmentInformationFile>();
        var writePolicyFile = provider.GetRequiredService<WriteWorkspacePolicyFragmentPolicyFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
            await writePolicyFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspacePolicyFragmentInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspacePolicyFragmentInformationFile);
    }

    private static WriteWorkspacePolicyFragmentInformationFile GetWriteWorkspacePolicyFragmentInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspacePolicyFragmentInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace policy fragment information file {WorkspacePolicyFragmentInformationFile}...", informationFile);

            // Remove policy contents from DTO, as these will be written to the policy file
            var updatedDto = dto with { Properties = dto.Properties with { Format = null, Value = null } };
            await informationFile.WriteDto(updatedDto, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspacePolicyFragmentPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspacePolicyFragmentPolicyFile);
    }

    private static WriteWorkspacePolicyFragmentPolicyFile GetWriteWorkspacePolicyFragmentPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var policyFile = WorkspacePolicyFragmentPolicyFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace policy fragment policy file {WorkspacePolicyFragmentPolicyFile}...", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}