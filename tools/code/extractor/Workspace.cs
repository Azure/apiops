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

internal static class WorkspaceModule
{
    public static void ConfigureExtractWorkspaces(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaces(builder);
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
                    .IterParallel(async workspace => await extractWorkspace(workspace.Name, workspace.Dto, cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractWorkspace(WorkspaceName name, WorkspaceDto dto, CancellationToken cancellationToken)
        {
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
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaces);
    }

    private static ListWorkspaces GetListWorkspaces(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<WorkspaceName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(WorkspaceName, WorkspaceDto)> listFromSet(IEnumerable<WorkspaceName> names, CancellationToken cancellationToken) =>
            names.Select(name => WorkspaceUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(WorkspaceName, WorkspaceDto)> listAll(CancellationToken cancellationToken)
        {
            var workspacesUri = WorkspacesUri.From(serviceUri);
            return workspacesUri.List(pipeline, cancellationToken);
        }
    }
}