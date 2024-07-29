using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractWorkspaces(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceName Name, WorkspaceDto Dto)> ListWorkspaces(CancellationToken cancellationToken);
public delegate bool ShouldExtractWorkspace(WorkspaceName name);
public delegate ValueTask WriteWorkspaceArtifacts(WorkspaceName name, WorkspaceDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceInformationFile(WorkspaceName name, WorkspaceDto dto, CancellationToken cancellationToken);

internal static class WorkspaceModule
{
    public static void ConfigureExtractWorkspaces(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaces(builder);
        ConfigureShouldExtractWorkspace(builder);
        ConfigureWriteWorkspaceArtifacts(builder);
        WorkspaceNamedValueModule.ConfigureExtractWorkspaceNamedValues(builder);
        WorkspaceBackendModule.ConfigureExtractWorkspaceBackends(builder);
        WorkspaceTagModule.ConfigureExtractWorkspaceTags(builder);
        WorkspaceVersionSetModule.ConfigureExtractWorkspaceVersionSets(builder);
        WorkspaceLoggerModule.ConfigureExtractWorkspaceLoggers(builder);
        WorkspaceDiagnosticModule.ConfigureExtractWorkspaceDiagnostics(builder);
        WorkspacePolicyFragmentModule.ConfigureExtractWorkspacePolicyFragments(builder);
        WorkspacePolicyModule.ConfigureExtractWorkspacePolicies(builder);
        WorkspaceProductModule.ConfigureExtractWorkspaceProducts(builder);
        WorkspaceGroupModule.ConfigureExtractWorkspaceGroups(builder);
        WorkspaceApiModule.ConfigureExtractWorkspaceApis(builder);
        WorkspaceSubscriptionModule.ConfigureExtractWorkspaceSubscriptions(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaces);
    }

    private static ExtractWorkspaces GetExtractWorkspaces(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaces>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractWorkspace>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceArtifacts>();
        var extractWorkspaceNamedValues = provider.GetRequiredService<ExtractWorkspaceNamedValues>();
        var extractWorkspaceBackends = provider.GetRequiredService<ExtractWorkspaceBackends>();
        var extractWorkspaceTags = provider.GetRequiredService<ExtractWorkspaceTags>();
        var extractWorkspaceVersionSets = provider.GetRequiredService<ExtractWorkspaceVersionSets>();
        var extractWorkspaceLoggers = provider.GetRequiredService<ExtractWorkspaceLoggers>();
        var extractWorkspaceDiagnostics = provider.GetRequiredService<ExtractWorkspaceDiagnostics>();
        var extractWorkspacePolicyFragments = provider.GetRequiredService<ExtractWorkspacePolicyFragments>();
        var extractWorkspacePolicies = provider.GetRequiredService<ExtractWorkspacePolicies>();
        var extractWorkspaceProducts = provider.GetRequiredService<ExtractWorkspaceProducts>();
        var extractWorkspaceGroups = provider.GetRequiredService<ExtractWorkspaceGroups>();
        var extractWorkspaceApis = provider.GetRequiredService<ExtractWorkspaceApis>();
        var extractWorkspaceSubscriptions = provider.GetRequiredService<ExtractWorkspaceSubscriptions>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaces));

            logger.LogInformation("Extracting workspaces...");

            await list(cancellationToken)
                    .Where(workspace => shouldExtract(workspace.Name))
                    .IterParallel(async workspace => await extractWorkspace(workspace.Name, workspace.Dto, cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractWorkspace(WorkspaceName name, WorkspaceDto dto, CancellationToken cancellationToken)
        {
            //await writeArtifacts(name, dto, cancellationToken); // TODO: Revisit support for writing workspace artifacts
            await extractWorkspaceNamedValues(name, cancellationToken);
            await extractWorkspaceBackends(name, cancellationToken);
            await extractWorkspaceTags(name, cancellationToken);
            await extractWorkspaceVersionSets(name, cancellationToken);
            await extractWorkspaceLoggers(name, cancellationToken);
            await extractWorkspaceDiagnostics(name, cancellationToken);
            await extractWorkspacePolicyFragments(name, cancellationToken);
            await extractWorkspacePolicies(name, cancellationToken);
            await extractWorkspaceProducts(name, cancellationToken);
            await extractWorkspaceGroups(name, cancellationToken);
            await extractWorkspaceApis(name, cancellationToken);
            await extractWorkspaceSubscriptions(name, cancellationToken);
        }
    }

    private static void ConfigureListWorkspaces(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaces);
    }

    private static ListWorkspaces GetListWorkspaces(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            WorkspacesUri.From(serviceUri)
                         .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractWorkspace(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractWorkspace);
    }

    private static ShouldExtractWorkspace GetShouldExtractWorkspace(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<WorkspaceName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteWorkspaceArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceArtifacts);
    }

    private static WriteWorkspaceArtifacts GetWriteWorkspaceArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceInformationFile);
    }

    private static WriteWorkspaceInformationFile GetWriteWorkspaceInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = WorkspaceInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing workspace information file {WorkspaceInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}