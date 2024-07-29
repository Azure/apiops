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

public delegate ValueTask ExtractWorkspaceDiagnostics(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(DiagnosticName Name, WorkspaceDiagnosticDto Dto)> ListWorkspaceDiagnostics(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceDiagnosticArtifacts(DiagnosticName name, WorkspaceDiagnosticDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceDiagnosticInformationFile(DiagnosticName name, WorkspaceDiagnosticDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceDiagnosticModule
{
    public static void ConfigureExtractWorkspaceDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceDiagnostics(builder);
        ConfigureWriteWorkspaceDiagnosticArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceDiagnostics);
    }

    private static ExtractWorkspaceDiagnostics GetExtractWorkspaceDiagnostics(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceDiagnostics>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceDiagnosticArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceDiagnostics));

            logger.LogInformation("Extracting diagnostics for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async diagnostic => await writeArtifacts(diagnostic.Name, diagnostic.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceDiagnostics(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceDiagnostics);
    }

    private static ListWorkspaceDiagnostics GetListWorkspaceDiagnostics(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspaceDiagnosticsUri.From(workspaceName, serviceUri)
                                   .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceDiagnosticArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceDiagnosticInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceDiagnosticArtifacts);
    }

    private static WriteWorkspaceDiagnosticArtifacts GetWriteWorkspaceDiagnosticArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceDiagnosticInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceDiagnosticInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceDiagnosticInformationFile);
    }

    private static WriteWorkspaceDiagnosticInformationFile GetWriteWorkspaceDiagnosticInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceDiagnosticInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace diagnostic information file {WorkspaceDiagnosticInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}