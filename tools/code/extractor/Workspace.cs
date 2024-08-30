using Azure.Core.Pipeline;
using common;
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
        WorkspaceApiModule.ConfigureExtractWorkspaceApis(builder);
        WorkspaceBackendModule.ConfigureExtractWorkspaceBackends(builder);
        WorkspaceDiagnosticModule.ConfigureExtractWorkspaceDiagnostics(builder);
        WorkspaceGroupModule.ConfigureExtractWorkspaceGroups(builder);
        WorkspaceLoggerModule.ConfigureExtractWorkspaceLoggers(builder);
        WorkspaceNamedValueModule.ConfigureExtractWorkspaceNamedValues(builder);
        WorkspacePolicyModule.ConfigureExtractWorkspacePolicies(builder);
        WorkspacePolicyFragmentModule.ConfigureExtractWorkspacePolicyFragments(builder);
        WorkspaceProductModule.ConfigureExtractWorkspaceProducts(builder);
        WorkspaceSubscriptionModule.ConfigureExtractWorkspaceSubscriptions(builder);
        WorkspaceTagModule.ConfigureExtractWorkspaceTags(builder);
        WorkspaceVersionSetModule.ConfigureExtractWorkspaceVersionSets(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaces);
    }

    private static ExtractWorkspaces GetExtractWorkspaces(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaces>();
        var extractApis = provider.GetRequiredService<ExtractWorkspaceApis>();
        var extractBackends = provider.GetRequiredService<ExtractWorkspaceBackends>();
        var extractDiagnostics = provider.GetRequiredService<ExtractWorkspaceDiagnostics>();
        var extractGroups = provider.GetRequiredService<ExtractWorkspaceGroups>();
        var extractLoggers = provider.GetRequiredService<ExtractWorkspaceLoggers>();
        var extractNamedValues = provider.GetRequiredService<ExtractWorkspaceNamedValues>();
        var extractPolicies = provider.GetRequiredService<ExtractWorkspacePolicies>();
        var extractPolicyFragments = provider.GetRequiredService<ExtractWorkspacePolicyFragments>();
        var extractProducts = provider.GetRequiredService<ExtractWorkspaceProducts>();
        var extractSubscriptions = provider.GetRequiredService<ExtractWorkspaceSubscriptions>();
        var extractTags = provider.GetRequiredService<ExtractWorkspaceTags>();
        var extractVersionSets = provider.GetRequiredService<ExtractWorkspaceVersionSets>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaces));

            logger.LogInformation("Extracting workspaces...");

            await list(cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await extractApis(resource.Name, cancellationToken);
                        await extractBackends(resource.Name, cancellationToken);
                        await extractDiagnostics(resource.Name, cancellationToken);
                        await extractGroups(resource.Name, cancellationToken);
                        await extractLoggers(resource.Name, cancellationToken);
                        await extractNamedValues(resource.Name, cancellationToken);
                        await extractPolicies(resource.Name, cancellationToken);
                        await extractPolicyFragments(resource.Name, cancellationToken);
                        await extractProducts(resource.Name, cancellationToken);
                        await extractSubscriptions(resource.Name, cancellationToken);
                        await extractTags(resource.Name, cancellationToken);
                        await extractVersionSets(resource.Name, cancellationToken);
                    }, cancellationToken);
        };
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

        var findConfigurationWorkspaces = findConfigurationNamesFactory.Create<WorkspaceName>();

        return cancellationToken =>
            findConfigurationWorkspaces()
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