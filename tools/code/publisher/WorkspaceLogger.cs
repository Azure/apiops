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
public delegate Option<(LoggerName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceLoggerName(FileInfo file);
public delegate bool IsWorkspaceLoggerNameInSourceControl(LoggerName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceLogger(LoggerName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceLoggerDto>> FindWorkspaceLoggerDto(LoggerName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceLoggerInApim(LoggerName name, WorkspaceLoggerDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceLoggers(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceLogger(LoggerName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceLoggerFromApim(LoggerName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceLoggerModule
{
    public static void ConfigurePutWorkspaceLoggers(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseLoggerName(builder);
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
                    .Where(logger => isNameInSourceControl(logger.Name, logger.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParseLoggerName(IHostApplicationBuilder builder)
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

        builder.Services.TryAddSingleton(GetIsLoggerNameInSourceControl);
    }

    private static IsWorkspaceLoggerNameInSourceControl GetIsLoggerNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(LoggerName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var loggerFile = WorkspaceLoggerInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(loggerFile.ToFileInfo());
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

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceLogger))
                                       ?.AddTag("workspace_logger.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
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

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceLoggerInformationFile.From(name, workspaceName, serviceDirectory);
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

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding logger {LoggerName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceLoggerUri.From(name, workspaceName, serviceUri)
                                    .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceLoggers(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseLoggerName(builder);
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
                    .Where(logger => isNameInSourceControl(logger.Name, logger.WorkspaceName) is false)
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

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceLogger))
                                       ?.AddTag("workspace_logger.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
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

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing logger {LoggerName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceLoggerUri.From(name, workspaceName, serviceUri)
                                    .Delete(pipeline, cancellationToken);
        };
    }
}