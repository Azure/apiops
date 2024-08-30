using Azure.Core.Pipeline;
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

public delegate ValueTask PutWorkspaceGroups(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceGroups(CancellationToken cancellationToken);
public delegate Option<(WorkspaceGroupName WorkspaceGroupName, WorkspaceName WorkspaceName)> TryParseWorkspaceGroupName(FileInfo file);
public delegate bool IsWorkspaceGroupNameInSourceControl(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceGroup(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceGroupDto>> FindWorkspaceGroupDto(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceGroupInApim(WorkspaceGroupName name, WorkspaceGroupDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceGroup(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceGroupFromApim(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceGroupModule
{
    public static void ConfigurePutWorkspaceGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceGroupName(builder);
        ConfigureIsWorkspaceGroupNameInSourceControl(builder);
        ConfigurePutWorkspaceGroup(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceGroups);
    }

    private static PutWorkspaceGroups GetPutWorkspaceGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceGroupNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceGroups));

            logger.LogInformation("Putting workspace groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceGroupName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceGroupName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceGroupName);
    }

    private static TryParseWorkspaceGroupName GetTryParseWorkspaceGroupName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceGroupInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceGroupNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceGroupNameInSourceControl);
    }

    private static IsWorkspaceGroupNameInSourceControl GetIsWorkspaceGroupNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceGroupInformationFile.From(workspaceGroupName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceGroup(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceGroupDto(builder);
        ConfigurePutWorkspaceGroupInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceGroup);
    }

    private static PutWorkspaceGroup GetPutWorkspaceGroup(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceGroupDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceGroupInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceGroupName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceGroup));

            var dtoOption = await findDto(workspaceGroupName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceGroupName, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceGroupDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceGroupDto);
    }

    private static FindWorkspaceGroupDto GetFindWorkspaceGroupDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceGroupName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceGroupInformationFile.From(workspaceGroupName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceGroupDto>();
        };
    }

    private static void ConfigurePutWorkspaceGroupInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceGroupInApim);
    }

    private static PutWorkspaceGroupInApim GetPutWorkspaceGroupInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceGroupName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting group {WorkspaceGroupName} in workspace {WorkspaceName}...", workspaceGroupName, workspaceName);

            var resourceUri = WorkspaceGroupUri.From(workspaceGroupName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceGroupName(builder);
        ConfigureIsWorkspaceGroupNameInSourceControl(builder);
        ConfigureDeleteWorkspaceGroup(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceGroups);
    }

    private static DeleteWorkspaceGroups GetDeleteWorkspaceGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceGroupNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceGroups));

            logger.LogInformation("Deleting workspace groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceGroupName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceGroup(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceGroupFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceGroup);
    }

    private static DeleteWorkspaceGroup GetDeleteWorkspaceGroup(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceGroupFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceGroupName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceGroup));

            await deleteFromApim(workspaceGroupName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceGroupFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceGroupFromApim);
    }

    private static DeleteWorkspaceGroupFromApim GetDeleteWorkspaceGroupFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceGroupName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting group {WorkspaceGroupName} in workspace {WorkspaceName}...", workspaceGroupName, workspaceName);

            var resourceUri = WorkspaceGroupUri.From(workspaceGroupName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}