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

public delegate ValueTask PutWorkspaceProductGroups(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductGroups(CancellationToken cancellationToken);
public delegate Option<(WorkspaceGroupName WorkspaceGroupName, WorkspaceProductName WorkspaceProductName, WorkspaceName WorkspaceName)> TryParseWorkspaceProductGroupName(FileInfo file);
public delegate bool IsWorkspaceProductGroupNameInSourceControl(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceProductGroup(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceProductGroupDto>> FindWorkspaceProductGroupDto(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceProductGroupInApim(WorkspaceGroupName name, WorkspaceProductGroupDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductGroup(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductGroupFromApim(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductGroupModule
{
    public static void ConfigurePutWorkspaceProductGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductGroupName(builder);
        ConfigureIsWorkspaceProductGroupNameInSourceControl(builder);
        ConfigurePutWorkspaceProductGroup(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductGroups);
    }

    private static PutWorkspaceProductGroups GetPutWorkspaceProductGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductGroupNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceProductGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProductGroups));

            logger.LogInformation("Putting workspace product groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceGroupName, resource.WorkspaceProductName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceGroupName, resource.WorkspaceProductName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceProductGroupName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceProductGroupName);
    }

    private static TryParseWorkspaceProductGroupName GetTryParseWorkspaceProductGroupName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceProductGroupInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name, informationFile.Parent.Parent.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceProductGroupNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceProductGroupNameInSourceControl);
    }

    private static IsWorkspaceProductGroupNameInSourceControl GetIsWorkspaceProductGroupNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceProductGroupInformationFile.From(workspaceGroupName, workspaceProductName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceProductGroup(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceProductGroupDto(builder);
        ConfigurePutWorkspaceProductGroupInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductGroup);
    }

    private static PutWorkspaceProductGroup GetPutWorkspaceProductGroup(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceProductGroupDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceProductGroupInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceGroupName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProductGroup));

            var dtoOption = await findDto(workspaceGroupName, workspaceProductName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceGroupName, dto, workspaceProductName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceProductGroupDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceProductGroupDto);
    }

    private static FindWorkspaceProductGroupDto GetFindWorkspaceProductGroupDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceGroupName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductGroupInformationFile.From(workspaceGroupName, workspaceProductName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceProductGroupDto>();
        };
    }

    private static void ConfigurePutWorkspaceProductGroupInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductGroupInApim);
    }

    private static PutWorkspaceProductGroupInApim GetPutWorkspaceProductGroupInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceGroupName, dto, workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting group {WorkspaceGroupName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceGroupName, workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductGroupUri.From(workspaceGroupName, workspaceProductName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceProductGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductGroupName(builder);
        ConfigureIsWorkspaceProductGroupNameInSourceControl(builder);
        ConfigureDeleteWorkspaceProductGroup(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductGroups);
    }

    private static DeleteWorkspaceProductGroups GetDeleteWorkspaceProductGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductGroupNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceProductGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProductGroups));

            logger.LogInformation("Deleting workspace product groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceGroupName, resource.WorkspaceProductName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceGroupName, resource.WorkspaceProductName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductGroup(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceProductGroupFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductGroup);
    }

    private static DeleteWorkspaceProductGroup GetDeleteWorkspaceProductGroup(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceProductGroupFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceGroupName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProductGroup));

            await deleteFromApim(workspaceGroupName, workspaceProductName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductGroupFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductGroupFromApim);
    }

    private static DeleteWorkspaceProductGroupFromApim GetDeleteWorkspaceProductGroupFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceGroupName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting group {WorkspaceGroupName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceGroupName, workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductGroupUri.From(workspaceGroupName, workspaceProductName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}