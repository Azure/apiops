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

public delegate ValueTask ExtractWorkspaceNamedValues(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(NamedValueName Name, WorkspaceNamedValueDto Dto)> ListWorkspaceNamedValues(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceNamedValueArtifacts(NamedValueName name, WorkspaceNamedValueDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceNamedValueInformationFile(NamedValueName name, WorkspaceNamedValueDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceNamedValueModule
{
    public static void ConfigureExtractWorkspaceNamedValues(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceNamedValues(builder);
        ConfigureWriteWorkspaceNamedValueArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceNamedValues);
    }

    private static ExtractWorkspaceNamedValues GetExtractWorkspaceNamedValues(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceNamedValues>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceNamedValueArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceNamedValues));

            logger.LogInformation("Extracting named values for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    .IterParallel(async namedValue => await writeArtifacts(namedValue.Name, namedValue.Dto, workspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListWorkspaceNamedValues(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceNamedValues);
    }

    private static ListWorkspaceNamedValues GetListWorkspaceNamedValues(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
            WorkspaceNamedValuesUri.From(workspaceName, serviceUri)
                                   .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteWorkspaceNamedValueArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceNamedValueInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceNamedValueArtifacts);
    }

    private static WriteWorkspaceNamedValueArtifacts GetWriteWorkspaceNamedValueArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceNamedValueInformationFile>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceNamedValueInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceNamedValueInformationFile);
    }

    private static WriteWorkspaceNamedValueInformationFile GetWriteWorkspaceNamedValueInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceNamedValueInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace named value information file {WorkspaceNamedValueInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}