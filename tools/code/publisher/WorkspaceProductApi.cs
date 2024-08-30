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

public delegate ValueTask PutWorkspaceProductApis(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductApis(CancellationToken cancellationToken);
public delegate Option<(WorkspaceApiName WorkspaceApiName, WorkspaceProductName WorkspaceProductName, WorkspaceName WorkspaceName)> TryParseWorkspaceProductApiName(FileInfo file);
public delegate bool IsWorkspaceProductApiNameInSourceControl(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceProductApi(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceProductApiDto>> FindWorkspaceProductApiDto(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceProductApiInApim(WorkspaceApiName name, WorkspaceProductApiDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductApi(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductApiFromApim(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductApiModule
{
    public static void ConfigurePutWorkspaceProductApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductApiName(builder);
        ConfigureIsWorkspaceProductApiNameInSourceControl(builder);
        ConfigurePutWorkspaceProductApi(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductApis);
    }

    private static PutWorkspaceProductApis GetPutWorkspaceProductApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductApiNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceProductApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProductApis));

            logger.LogInformation("Putting workspace product APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiName, resource.WorkspaceProductName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceApiName, resource.WorkspaceProductName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceProductApiName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceProductApiName);
    }

    private static TryParseWorkspaceProductApiName GetTryParseWorkspaceProductApiName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceProductApiInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name, informationFile.Parent.Parent.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceProductApiNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceProductApiNameInSourceControl);
    }

    private static IsWorkspaceProductApiNameInSourceControl GetIsWorkspaceProductApiNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceProductApiInformationFile.From(workspaceApiName, workspaceProductName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceProductApi(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceProductApiDto(builder);
        ConfigurePutWorkspaceProductApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductApi);
    }

    private static PutWorkspaceProductApi GetPutWorkspaceProductApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceProductApiDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceProductApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProductApi));

            var dtoOption = await findDto(workspaceApiName, workspaceProductName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceApiName, dto, workspaceProductName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceProductApiDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceProductApiDto);
    }

    private static FindWorkspaceProductApiDto GetFindWorkspaceProductApiDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductApiInformationFile.From(workspaceApiName, workspaceProductName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceProductApiDto>();
        };
    }

    private static void ConfigurePutWorkspaceProductApiInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductApiInApim);
    }

    private static PutWorkspaceProductApiInApim GetPutWorkspaceProductApiInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, dto, workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting API {WorkspaceApiName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceApiName, workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductApiUri.From(workspaceApiName, workspaceProductName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceProductApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductApiName(builder);
        ConfigureIsWorkspaceProductApiNameInSourceControl(builder);
        ConfigureDeleteWorkspaceProductApi(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductApis);
    }

    private static DeleteWorkspaceProductApis GetDeleteWorkspaceProductApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductApiNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceProductApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProductApis));

            logger.LogInformation("Deleting workspace product APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiName, resource.WorkspaceProductName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceApiName, resource.WorkspaceProductName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductApi(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceProductApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductApi);
    }

    private static DeleteWorkspaceProductApi GetDeleteWorkspaceProductApi(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceProductApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProductApi));

            await deleteFromApim(workspaceApiName, workspaceProductName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductApiFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductApiFromApim);
    }

    private static DeleteWorkspaceProductApiFromApim GetDeleteWorkspaceProductApiFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting API {WorkspaceApiName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceApiName, workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductApiUri.From(workspaceApiName, workspaceProductName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}