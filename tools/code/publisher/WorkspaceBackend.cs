﻿using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutWorkspaceBackends(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceBackends(CancellationToken cancellationToken);
public delegate Option<(WorkspaceBackendName WorkspaceBackendName, WorkspaceName WorkspaceName)> TryParseWorkspaceBackendName(FileInfo file);
public delegate bool IsWorkspaceBackendNameInSourceControl(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceBackend(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceBackendDto>> FindWorkspaceBackendDto(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceBackendInApim(WorkspaceBackendName name, WorkspaceBackendDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceBackend(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceBackendFromApim(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceBackendModule
{
    public static void ConfigurePutWorkspaceBackends(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceBackendName(builder);
        ConfigureIsWorkspaceBackendNameInSourceControl(builder);
        ConfigurePutWorkspaceBackend(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceBackends);
    }

    private static PutWorkspaceBackends GetPutWorkspaceBackends(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceBackendName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceBackendNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceBackend>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceBackends));

            logger.LogInformation("Putting workspace backends...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceBackendName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceBackendName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceBackendName);
    }

    private static TryParseWorkspaceBackendName GetTryParseWorkspaceBackendName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceBackendInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceBackendNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceBackendNameInSourceControl);
    }

    private static IsWorkspaceBackendNameInSourceControl GetIsWorkspaceBackendNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceBackendName workspaceBackendName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceBackendInformationFile.From(workspaceBackendName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceBackend(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceBackendDto(builder);
        ConfigurePutWorkspaceBackendInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceBackend);
    }

    private static PutWorkspaceBackend GetPutWorkspaceBackend(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceBackendDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceBackendInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceBackendName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceBackend));

            var dtoOption = await findDto(workspaceBackendName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceBackendName, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceBackendDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceBackendDto);
    }

    private static FindWorkspaceBackendDto GetFindWorkspaceBackendDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceBackendName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceBackendInformationFile.From(workspaceBackendName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceBackendDto>();
        };
    }

    private static void ConfigurePutWorkspaceBackendInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceBackendInApim);
    }

    private static PutWorkspaceBackendInApim GetPutWorkspaceBackendInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceBackendName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting backend {WorkspaceBackendName} in workspace {WorkspaceName}...", workspaceBackendName, workspaceName);

            var resourceUri = WorkspaceBackendUri.From(workspaceBackendName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceBackends(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceBackendName(builder);
        ConfigureIsWorkspaceBackendNameInSourceControl(builder);
        ConfigureDeleteWorkspaceBackend(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceBackends);
    }

    private static DeleteWorkspaceBackends GetDeleteWorkspaceBackends(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceBackendName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceBackendNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceBackend>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceBackends));

            logger.LogInformation("Deleting workspace backends...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceBackendName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceBackend(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceBackendFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceBackend);
    }

    private static DeleteWorkspaceBackend GetDeleteWorkspaceBackend(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceBackendFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceBackendName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceBackend));

            await deleteFromApim(workspaceBackendName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceBackendFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceBackendFromApim);
    }

    private static DeleteWorkspaceBackendFromApim GetDeleteWorkspaceBackendFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceBackendName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting backend {WorkspaceBackendName} in workspace {WorkspaceName}...", workspaceBackendName, workspaceName);

            var resourceUri = WorkspaceBackendUri.From(workspaceBackendName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}
