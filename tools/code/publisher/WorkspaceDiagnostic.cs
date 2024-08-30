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

public delegate ValueTask PutWorkspaceDiagnostics(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceDiagnostics(CancellationToken cancellationToken);
public delegate Option<(WorkspaceDiagnosticName WorkspaceDiagnosticName, WorkspaceName WorkspaceName)> TryParseWorkspaceDiagnosticName(FileInfo file);
public delegate bool IsWorkspaceDiagnosticNameInSourceControl(WorkspaceDiagnosticName workspaceDiagnosticName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceDiagnostic(WorkspaceDiagnosticName workspaceDiagnosticName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceDiagnosticDto>> FindWorkspaceDiagnosticDto(WorkspaceDiagnosticName workspaceDiagnosticName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceDiagnosticInApim(WorkspaceDiagnosticName name, WorkspaceDiagnosticDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceDiagnostic(WorkspaceDiagnosticName workspaceDiagnosticName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceDiagnosticFromApim(WorkspaceDiagnosticName workspaceDiagnosticName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceDiagnosticModule
{
    public static void ConfigurePutWorkspaceDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceDiagnosticName(builder);
        ConfigureIsWorkspaceDiagnosticNameInSourceControl(builder);
        ConfigurePutWorkspaceDiagnostic(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceDiagnostics);
    }

    private static PutWorkspaceDiagnostics GetPutWorkspaceDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceDiagnosticNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceDiagnostics));

            logger.LogInformation("Putting workspace diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceDiagnosticName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceDiagnosticName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceDiagnosticName);
    }

    private static TryParseWorkspaceDiagnosticName GetTryParseWorkspaceDiagnosticName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceDiagnosticInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceDiagnosticNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceDiagnosticNameInSourceControl);
    }

    private static IsWorkspaceDiagnosticNameInSourceControl GetIsWorkspaceDiagnosticNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceDiagnosticName workspaceDiagnosticName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceDiagnosticInformationFile.From(workspaceDiagnosticName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceDiagnosticDto(builder);
        ConfigurePutWorkspaceDiagnosticInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceDiagnostic);
    }

    private static PutWorkspaceDiagnostic GetPutWorkspaceDiagnostic(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceDiagnosticDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceDiagnosticInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceDiagnosticName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceDiagnostic));

            var dtoOption = await findDto(workspaceDiagnosticName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceDiagnosticName, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceDiagnosticDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceDiagnosticDto);
    }

    private static FindWorkspaceDiagnosticDto GetFindWorkspaceDiagnosticDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceDiagnosticName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceDiagnosticInformationFile.From(workspaceDiagnosticName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceDiagnosticDto>();
        };
    }

    private static void ConfigurePutWorkspaceDiagnosticInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceDiagnosticInApim);
    }

    private static PutWorkspaceDiagnosticInApim GetPutWorkspaceDiagnosticInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceDiagnosticName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting diagnostic {WorkspaceDiagnosticName} in workspace {WorkspaceName}...", workspaceDiagnosticName, workspaceName);

            var resourceUri = WorkspaceDiagnosticUri.From(workspaceDiagnosticName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceDiagnosticName(builder);
        ConfigureIsWorkspaceDiagnosticNameInSourceControl(builder);
        ConfigureDeleteWorkspaceDiagnostic(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceDiagnostics);
    }

    private static DeleteWorkspaceDiagnostics GetDeleteWorkspaceDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceDiagnosticNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceDiagnostics));

            logger.LogInformation("Deleting workspace diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceDiagnosticName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceDiagnosticFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceDiagnostic);
    }

    private static DeleteWorkspaceDiagnostic GetDeleteWorkspaceDiagnostic(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceDiagnosticFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceDiagnosticName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceDiagnostic));

            await deleteFromApim(workspaceDiagnosticName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceDiagnosticFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceDiagnosticFromApim);
    }

    private static DeleteWorkspaceDiagnosticFromApim GetDeleteWorkspaceDiagnosticFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceDiagnosticName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting diagnostic {WorkspaceDiagnosticName} in workspace {WorkspaceName}...", workspaceDiagnosticName, workspaceName);

            var resourceUri = WorkspaceDiagnosticUri.From(workspaceDiagnosticName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}