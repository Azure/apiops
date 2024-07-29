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

public delegate ValueTask PutWorkspaceTags(CancellationToken cancellationToken);
public delegate Option<(TagName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceTagName(FileInfo file);
public delegate bool IsWorkspaceTagNameInSourceControl(TagName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceTag(TagName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceTagDto>> FindWorkspaceTagDto(TagName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceTagInApim(TagName name, WorkspaceTagDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTags(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTag(TagName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagFromApim(TagName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceTagModule
{
    public static void ConfigurePutWorkspaceTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseTagName(builder);
        ConfigureIsWorkspaceTagNameInSourceControl(builder);
        ConfigurePutWorkspaceTag(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTags);
    }

    private static PutWorkspaceTags GetPutWorkspaceTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceTagNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceTags));

            logger.LogInformation("Putting workspace tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParseTagName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceTagName);
    }

    private static TryParseWorkspaceTagName GetTryParseWorkspaceTagName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceTagInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceTagNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsTagNameInSourceControl);
    }

    private static IsWorkspaceTagNameInSourceControl GetIsTagNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(TagName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var tagFile = WorkspaceTagInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(tagFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceTag(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceTagDto(builder);
        ConfigurePutWorkspaceTagInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTag);
    }

    private static PutWorkspaceTag GetPutWorkspaceTag(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceTagDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceTagInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceTag))
                                       ?.AddTag("workspace_tag.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceTagDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceTagDto);
    }

    private static FindWorkspaceTagDto GetFindWorkspaceTagDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceTagInformationFile.From(name, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceTagDto>();
        };
    }

    private static void ConfigurePutWorkspaceTagInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagInApim);
    }

    private static PutWorkspaceTagInApim GetPutWorkspaceTagInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding tag {TagName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceTagUri.From(name, workspaceName, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseTagName(builder);
        ConfigureIsWorkspaceTagNameInSourceControl(builder);
        ConfigureDeleteWorkspaceTag(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTags);
    }

    private static DeleteWorkspaceTags GetDeleteWorkspaceTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceTagNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceTags));

            logger.LogInformation("Deleting workspace tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceTag(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceTagFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTag);
    }

    private static DeleteWorkspaceTag GetDeleteWorkspaceTag(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceTagFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceTag))
                                       ?.AddTag("workspace_tag.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceTagFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagFromApim);
    }

    private static DeleteWorkspaceTagFromApim GetDeleteWorkspaceTagFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing tag {TagName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceTagUri.From(name, workspaceName, serviceUri)
                                 .Delete(pipeline, cancellationToken);
        };
    }
}