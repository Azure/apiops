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

public delegate ValueTask PutWorkspaceLoggers(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceLoggers(CancellationToken cancellationToken);
public delegate Option<(WorkspaceLoggerName WorkspaceLoggerName, WorkspaceName WorkspaceName)> TryParseWorkspaceLoggerName(FileInfo file);
public delegate bool IsWorkspaceLoggerNameInSourceControl(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceLogger(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceLoggerDto>> FindWorkspaceLoggerDto(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceLoggerInApim(WorkspaceLoggerName name, WorkspaceLoggerDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceLogger(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceLoggerFromApim(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceLoggerModule
{
    public static void ConfigurePutWorkspaceLoggers(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceLoggerName(builder);
        ConfigureIsWorkspaceLoggerNameInSourceControl(builder);
        ConfigurePutWorkspaceLogger(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceLoggers);
    }

    private static PutWorkspaceLoggers GetPutWorkspaceLoggers(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceLoggerName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceLoggerNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceLogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceLoggers));

            logger.LogInformation("Putting workspace loggers...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceLoggerName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceLoggerName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceLoggerName);
    }

    private static TryParseWorkspaceLoggerName GetTryParseWorkspaceLoggerName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceLoggerInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceLoggerNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceLoggerNameInSourceControl);
    }

    private static IsWorkspaceLoggerNameInSourceControl GetIsWorkspaceLoggerNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceLoggerInformationFile.From(workspaceLoggerName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceLogger(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceLoggerDto(builder);
        ConfigurePutWorkspaceLoggerInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceLogger);
    }

    private static PutWorkspaceLogger GetPutWorkspaceLogger(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceLoggerDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceLoggerInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceLoggerName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceLogger));

            var dtoOption = await findDto(workspaceLoggerName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceLoggerName, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceLoggerDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceLoggerDto);
    }

    private static FindWorkspaceLoggerDto GetFindWorkspaceLoggerDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceLoggerName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceLoggerInformationFile.From(workspaceLoggerName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceLoggerDto>();
        };
    }

    private static void ConfigurePutWorkspaceLoggerInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceLoggerInApim);
    }

    private static PutWorkspaceLoggerInApim GetPutWorkspaceLoggerInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceLoggerName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting logger {WorkspaceLoggerName} in workspace {WorkspaceName}...", workspaceLoggerName, workspaceName);

            var resourceUri = WorkspaceLoggerUri.From(workspaceLoggerName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceLoggers(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceLoggerName(builder);
        ConfigureIsWorkspaceLoggerNameInSourceControl(builder);
        ConfigureDeleteWorkspaceLogger(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceLoggers);
    }

    private static DeleteWorkspaceLoggers GetDeleteWorkspaceLoggers(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceLoggerName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceLoggerNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceLogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceLoggers));

            logger.LogInformation("Deleting workspace loggers...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceLoggerName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceLogger(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceLoggerFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceLogger);
    }

    private static DeleteWorkspaceLogger GetDeleteWorkspaceLogger(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceLoggerFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceLoggerName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceLogger));

            await deleteFromApim(workspaceLoggerName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceLoggerFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceLoggerFromApim);
    }

    private static DeleteWorkspaceLoggerFromApim GetDeleteWorkspaceLoggerFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceLoggerName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting logger {WorkspaceLoggerName} in workspace {WorkspaceName}...", workspaceLoggerName, workspaceName);

            var resourceUri = WorkspaceLoggerUri.From(workspaceLoggerName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}