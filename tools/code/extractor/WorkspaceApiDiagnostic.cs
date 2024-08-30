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

public delegate ValueTask ExtractWorkspaceApiDiagnostics(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(WorkspaceApiDiagnosticName Name, WorkspaceApiDiagnosticDto Dto)> ListWorkspaceApiDiagnostics(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiDiagnosticArtifacts(WorkspaceApiDiagnosticName name, WorkspaceApiDiagnosticDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiDiagnosticInformationFile(WorkspaceApiDiagnosticName name, WorkspaceApiDiagnosticDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiDiagnosticModule
{
    public static void ConfigureExtractWorkspaceApiDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceApiDiagnostics(builder);
        ConfigureWriteWorkspaceApiDiagnosticArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceApiDiagnostics);
    }

    private static ExtractWorkspaceApiDiagnostics GetExtractWorkspaceApiDiagnostics(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceApiDiagnostics>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceApiDiagnosticArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceApiDiagnostics));

            logger.LogInformation("Extracting diagnostics in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiName, workspaceName);

            await list(workspaceApiName, workspaceName, cancellationToken)
                    .IterParallel(async resource =>
                    {
                        await writeArtifacts(resource.Name, resource.Dto, workspaceApiName, workspaceName, cancellationToken);
                    }, cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceApiDiagnostics(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceApiDiagnostics);
    }

    private static ListWorkspaceApiDiagnostics GetListWorkspaceApiDiagnostics(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceApiName, workspaceName, cancellationToken) =>
        {
            var workspaceApiDiagnosticsUri = WorkspaceApiDiagnosticsUri.From(workspaceApiName, workspaceName, serviceUri);
            return workspaceApiDiagnosticsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceApiDiagnosticArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceApiDiagnosticInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiDiagnosticArtifacts);
    }

    private static WriteWorkspaceApiDiagnosticArtifacts GetWriteWorkspaceApiDiagnosticArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceApiDiagnosticInformationFile>();

        return async (name, dto, workspaceApiName, workspaceName, cancellationToken) =>
            await writeInformationFile(name, dto, workspaceApiName, workspaceName, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceApiDiagnosticInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiDiagnosticInformationFile);
    }

    private static WriteWorkspaceApiDiagnosticInformationFile GetWriteWorkspaceApiDiagnosticInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceApiDiagnosticInformationFile.From(name, workspaceApiName, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace API diagnostic information file {WorkspaceApiDiagnosticInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}