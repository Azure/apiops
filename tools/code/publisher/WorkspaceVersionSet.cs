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
public delegate ValueTask DeleteWorkspaceVersionSets(CancellationToken cancellationToken);
public delegate Option<(WorkspaceVersionSetName WorkspaceVersionSetName, WorkspaceName WorkspaceName)> TryParseWorkspaceVersionSetName(FileInfo file);
public delegate bool IsWorkspaceVersionSetNameInSourceControl(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceVersionSet(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceVersionSetDto>> FindWorkspaceVersionSetDto(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceVersionSetInApim(WorkspaceVersionSetName name, WorkspaceVersionSetDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceVersionSet(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceVersionSetFromApim(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceVersionSetModule
{
    public static void ConfigurePutWorkspaceVersionSets(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceVersionSetName(builder);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceVersionSetName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceVersionSetName(IHostApplicationBuilder builder)
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

        builder.Services.TryAddSingleton(GetIsWorkspaceVersionSetNameInSourceControl);
    }

    private static IsWorkspaceVersionSetNameInSourceControl GetIsWorkspaceVersionSetNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceVersionSetInformationFile.From(workspaceVersionSetName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
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

        return async (workspaceVersionSetName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceVersionSet));

            var dtoOption = await findDto(workspaceVersionSetName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceVersionSetName, dto, workspaceName, cancellationToken));
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

        return async (workspaceVersionSetName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceVersionSetInformationFile.From(workspaceVersionSetName, workspaceName, serviceDirectory);
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

        return async (workspaceVersionSetName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting version set {WorkspaceVersionSetName} in workspace {WorkspaceName}...", workspaceVersionSetName, workspaceName);

            var resourceUri = WorkspaceVersionSetUri.From(workspaceVersionSetName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceVersionSets(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceVersionSetName(builder);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceVersionSetName, resource.WorkspaceName) is false)
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

        return async (workspaceVersionSetName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceVersionSet));

            await deleteFromApim(workspaceVersionSetName, workspaceName, cancellationToken);
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

        return async (workspaceVersionSetName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting version set {WorkspaceVersionSetName} in workspace {WorkspaceName}...", workspaceVersionSetName, workspaceName);

            var resourceUri = WorkspaceVersionSetUri.From(workspaceVersionSetName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}