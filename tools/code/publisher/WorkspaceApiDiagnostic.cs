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

public delegate ValueTask PutWorkspaceApiDiagnostics(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiDiagnostics(CancellationToken cancellationToken);
public delegate Option<(WorkspaceApiDiagnosticName WorkspaceApiDiagnosticName, WorkspaceApiName WorkspaceApiName, WorkspaceName WorkspaceName)> TryParseWorkspaceApiDiagnosticName(FileInfo file);
public delegate bool IsWorkspaceApiDiagnosticNameInSourceControl(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceApiDiagnostic(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceApiDiagnosticDto>> FindWorkspaceApiDiagnosticDto(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceApiDiagnosticInApim(WorkspaceApiDiagnosticName name, WorkspaceApiDiagnosticDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiDiagnostic(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiDiagnosticFromApim(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiDiagnosticModule
{
    public static void ConfigurePutWorkspaceApiDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiDiagnosticName(builder);
        ConfigureIsWorkspaceApiDiagnosticNameInSourceControl(builder);
        ConfigurePutWorkspaceApiDiagnostic(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiDiagnostics);
    }

    private static PutWorkspaceApiDiagnostics GetPutWorkspaceApiDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiDiagnosticNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceApiDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApiDiagnostics));

            logger.LogInformation("Putting workspace API diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiDiagnosticName, resource.WorkspaceApiName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceApiDiagnosticName, resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceApiDiagnosticName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceApiDiagnosticName);
    }

    private static TryParseWorkspaceApiDiagnosticName GetTryParseWorkspaceApiDiagnosticName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceApiDiagnosticInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name, informationFile.Parent.Parent.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceApiDiagnosticNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceApiDiagnosticNameInSourceControl);
    }

    private static IsWorkspaceApiDiagnosticNameInSourceControl GetIsWorkspaceApiDiagnosticNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceApiDiagnosticInformationFile.From(workspaceApiDiagnosticName, workspaceApiName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceApiDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiDiagnosticDto(builder);
        ConfigurePutWorkspaceApiDiagnosticInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiDiagnostic);
    }

    private static PutWorkspaceApiDiagnostic GetPutWorkspaceApiDiagnostic(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiDiagnosticDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceApiDiagnosticInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiDiagnosticName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApiDiagnostic));

            var dtoOption = await findDto(workspaceApiDiagnosticName, workspaceApiName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceApiDiagnosticName, dto, workspaceApiName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceApiDiagnosticDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiDiagnosticDto);
    }

    private static FindWorkspaceApiDiagnosticDto GetFindWorkspaceApiDiagnosticDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiDiagnosticName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceApiDiagnosticInformationFile.From(workspaceApiDiagnosticName, workspaceApiName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceApiDiagnosticDto>();
        };
    }

    private static void ConfigurePutWorkspaceApiDiagnosticInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiDiagnosticInApim);
    }

    private static PutWorkspaceApiDiagnosticInApim GetPutWorkspaceApiDiagnosticInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiDiagnosticName, dto, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting diagnostic {WorkspaceApiDiagnosticName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiDiagnosticName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiDiagnosticUri.From(workspaceApiDiagnosticName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceApiDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiDiagnosticName(builder);
        ConfigureIsWorkspaceApiDiagnosticNameInSourceControl(builder);
        ConfigureDeleteWorkspaceApiDiagnostic(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiDiagnostics);
    }

    private static DeleteWorkspaceApiDiagnostics GetDeleteWorkspaceApiDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiDiagnosticNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceApiDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApiDiagnostics));

            logger.LogInformation("Deleting workspace API diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiDiagnosticName, resource.WorkspaceApiName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceApiDiagnosticName, resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApiDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceApiDiagnosticFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiDiagnostic);
    }

    private static DeleteWorkspaceApiDiagnostic GetDeleteWorkspaceApiDiagnostic(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceApiDiagnosticFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiDiagnosticName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApiDiagnostic));

            await deleteFromApim(workspaceApiDiagnosticName, workspaceApiName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApiDiagnosticFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiDiagnosticFromApim);
    }

    private static DeleteWorkspaceApiDiagnosticFromApim GetDeleteWorkspaceApiDiagnosticFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiDiagnosticName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting diagnostic {WorkspaceApiDiagnosticName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiDiagnosticName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiDiagnosticUri.From(workspaceApiDiagnosticName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}