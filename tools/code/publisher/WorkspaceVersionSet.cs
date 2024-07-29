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

public delegate ValueTask PutWorkspaceVersionSets(CancellationToken cancellationToken);
public delegate Option<(VersionSetName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceVersionSetName(FileInfo file);
public delegate bool IsWorkspaceVersionSetNameInSourceControl(VersionSetName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceVersionSet(VersionSetName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceVersionSetDto>> FindWorkspaceVersionSetDto(VersionSetName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceVersionSetInApim(VersionSetName name, WorkspaceVersionSetDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceVersionSets(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceVersionSet(VersionSetName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceVersionSetFromApim(VersionSetName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceVersionSetModule
{
    public static void ConfigurePutWorkspaceVersionSets(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseVersionSetName(builder);
        ConfigureIsWorkspaceVersionSetNameInSourceControl(builder);
        ConfigurePutWorkspaceVersionSet(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceVersionSets);
    }

    private static PutWorkspaceVersionSets GetPutWorkspaceVersionSets(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceVersionSetName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceVersionSetNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceVersionSet>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceVersionSets));

            logger.LogInformation("Putting workspace version sets...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParseVersionSetName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceVersionSetName);
    }

    private static TryParseWorkspaceVersionSetName GetTryParseWorkspaceVersionSetName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceVersionSetInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceVersionSetNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsVersionSetNameInSourceControl);
    }

    private static IsWorkspaceVersionSetNameInSourceControl GetIsVersionSetNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(VersionSetName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var tagFile = WorkspaceVersionSetInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(tagFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceVersionSet(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceVersionSetDto(builder);
        ConfigurePutWorkspaceVersionSetInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceVersionSet);
    }

    private static PutWorkspaceVersionSet GetPutWorkspaceVersionSet(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceVersionSetDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceVersionSetInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceVersionSet))
                                       ?.AddTag("workspace_version_set.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceVersionSetDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceVersionSetDto);
    }

    private static FindWorkspaceVersionSetDto GetFindWorkspaceVersionSetDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceVersionSetInformationFile.From(name, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceVersionSetDto>();
        };
    }

    private static void ConfigurePutWorkspaceVersionSetInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceVersionSetInApim);
    }

    private static PutWorkspaceVersionSetInApim GetPutWorkspaceVersionSetInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding version set {VersionSetName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceVersionSetUri.From(name, workspaceName, serviceUri)
                                        .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceVersionSets(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseVersionSetName(builder);
        ConfigureIsWorkspaceVersionSetNameInSourceControl(builder);
        ConfigureDeleteWorkspaceVersionSet(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceVersionSets);
    }

    private static DeleteWorkspaceVersionSets GetDeleteWorkspaceVersionSets(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceVersionSetName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceVersionSetNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceVersionSet>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceVersionSets));

            logger.LogInformation("Deleting workspace version sets...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceVersionSet(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceVersionSetFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceVersionSet);
    }

    private static DeleteWorkspaceVersionSet GetDeleteWorkspaceVersionSet(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceVersionSetFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceVersionSet))
                                       ?.AddTag("workspace_version_set.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceVersionSetFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceVersionSetFromApim);
    }

    private static DeleteWorkspaceVersionSetFromApim GetDeleteWorkspaceVersionSetFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing version set {VersionSetName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceVersionSetUri.From(name, workspaceName, serviceUri)
                                        .Delete(pipeline, cancellationToken);
        };
    }
}