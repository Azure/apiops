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
public delegate Option<(GroupName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceGroupName(FileInfo file);
public delegate bool IsWorkspaceGroupNameInSourceControl(GroupName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceGroup(GroupName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceGroupDto>> FindWorkspaceGroupDto(GroupName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceGroupInApim(GroupName name, WorkspaceGroupDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceGroups(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceGroup(GroupName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceGroupFromApim(GroupName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceGroupModule
{
    public static void ConfigurePutWorkspaceGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseGroupName(builder);
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
                    .Where(group => isNameInSourceControl(group.Name, group.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParseGroupName(IHostApplicationBuilder builder)
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

        builder.Services.TryAddSingleton(GetIsGroupNameInSourceControl);
    }

    private static IsWorkspaceGroupNameInSourceControl GetIsGroupNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(GroupName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var groupFile = WorkspaceGroupInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(groupFile.ToFileInfo());
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

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceGroup))
                                       ?.AddTag("workspace_group.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
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

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceGroupInformationFile.From(name, workspaceName, serviceDirectory);
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

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding group {GroupName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceGroupUri.From(name, workspaceName, serviceUri)
                                   .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseGroupName(builder);
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
                    .Where(group => isNameInSourceControl(group.Name, group.WorkspaceName) is false)
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

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceGroup))
                                       ?.AddTag("workspace_group.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
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

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing group {GroupName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceGroupUri.From(name, workspaceName, serviceUri)
                                   .Delete(pipeline, cancellationToken);
        };
    }
}