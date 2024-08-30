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

public delegate ValueTask PutWorkspaceSubscriptions(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceSubscriptions(CancellationToken cancellationToken);
public delegate Option<(WorkspaceSubscriptionName WorkspaceSubscriptionName, WorkspaceName WorkspaceName)> TryParseWorkspaceSubscriptionName(FileInfo file);
public delegate bool IsWorkspaceSubscriptionNameInSourceControl(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceSubscription(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceSubscriptionDto>> FindWorkspaceSubscriptionDto(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceSubscriptionInApim(WorkspaceSubscriptionName name, WorkspaceSubscriptionDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceSubscription(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceSubscriptionFromApim(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceSubscriptionModule
{
    public static void ConfigurePutWorkspaceSubscriptions(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceSubscriptionName(builder);
        ConfigureIsWorkspaceSubscriptionNameInSourceControl(builder);
        ConfigurePutWorkspaceSubscription(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceSubscriptions);
    }

    private static PutWorkspaceSubscriptions GetPutWorkspaceSubscriptions(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceSubscriptionName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceSubscriptionNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceSubscription>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceSubscriptions));

            logger.LogInformation("Putting workspace subscriptions...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceSubscriptionName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceSubscriptionName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceSubscriptionName);
    }

    private static TryParseWorkspaceSubscriptionName GetTryParseWorkspaceSubscriptionName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceSubscriptionInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceSubscriptionNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceSubscriptionNameInSourceControl);
    }

    private static IsWorkspaceSubscriptionNameInSourceControl GetIsWorkspaceSubscriptionNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceSubscriptionInformationFile.From(workspaceSubscriptionName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceSubscription(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceSubscriptionDto(builder);
        ConfigurePutWorkspaceSubscriptionInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceSubscription);
    }

    private static PutWorkspaceSubscription GetPutWorkspaceSubscription(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceSubscriptionDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceSubscriptionInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceSubscriptionName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceSubscription));

            var dtoOption = await findDto(workspaceSubscriptionName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceSubscriptionName, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceSubscriptionDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceSubscriptionDto);
    }

    private static FindWorkspaceSubscriptionDto GetFindWorkspaceSubscriptionDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceSubscriptionName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceSubscriptionInformationFile.From(workspaceSubscriptionName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceSubscriptionDto>();
        };
    }

    private static void ConfigurePutWorkspaceSubscriptionInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceSubscriptionInApim);
    }

    private static PutWorkspaceSubscriptionInApim GetPutWorkspaceSubscriptionInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceSubscriptionName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting subscription {WorkspaceSubscriptionName} in workspace {WorkspaceName}...", workspaceSubscriptionName, workspaceName);

            var resourceUri = WorkspaceSubscriptionUri.From(workspaceSubscriptionName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceSubscriptions(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceSubscriptionName(builder);
        ConfigureIsWorkspaceSubscriptionNameInSourceControl(builder);
        ConfigureDeleteWorkspaceSubscription(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceSubscriptions);
    }

    private static DeleteWorkspaceSubscriptions GetDeleteWorkspaceSubscriptions(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceSubscriptionName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceSubscriptionNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceSubscription>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceSubscriptions));

            logger.LogInformation("Deleting workspace subscriptions...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceSubscriptionName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceSubscription(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceSubscriptionFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceSubscription);
    }

    private static DeleteWorkspaceSubscription GetDeleteWorkspaceSubscription(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceSubscriptionFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceSubscriptionName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceSubscription));

            await deleteFromApim(workspaceSubscriptionName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceSubscriptionFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceSubscriptionFromApim);
    }

    private static DeleteWorkspaceSubscriptionFromApim GetDeleteWorkspaceSubscriptionFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceSubscriptionName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting subscription {WorkspaceSubscriptionName} in workspace {WorkspaceName}...", workspaceSubscriptionName, workspaceName);

            var resourceUri = WorkspaceSubscriptionUri.From(workspaceSubscriptionName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}