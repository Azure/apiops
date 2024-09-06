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

public delegate ValueTask PutWorkspaceTagApis(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagApis(CancellationToken cancellationToken);
public delegate Option<(WorkspaceApiName WorkspaceApiName, WorkspaceTagName WorkspaceTagName, WorkspaceName WorkspaceName)> TryParseWorkspaceTagApiName(FileInfo file);
public delegate bool IsWorkspaceTagApiNameInSourceControl(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceTagApi(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceTagApiDto>> FindWorkspaceTagApiDto(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceTagApiInApim(WorkspaceApiName name, WorkspaceTagApiDto dto, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagApi(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagApiFromApim(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceTagApiModule
{
    public static void ConfigurePutWorkspaceTagApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceTagApiName(builder);
        ConfigureIsWorkspaceTagApiNameInSourceControl(builder);
        ConfigurePutWorkspaceTagApi(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagApis);
    }

    private static PutWorkspaceTagApis GetPutWorkspaceTagApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceTagApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceTagApiNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceTagApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceTagApis));

            logger.LogInformation("Putting workspace tag APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiName, resource.WorkspaceTagName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceApiName, resource.WorkspaceTagName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceTagApiName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceTagApiName);
    }

    private static TryParseWorkspaceTagApiName GetTryParseWorkspaceTagApiName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceTagApiInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name, informationFile.Parent.Parent.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceTagApiNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceTagApiNameInSourceControl);
    }

    private static IsWorkspaceTagApiNameInSourceControl GetIsWorkspaceTagApiNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceTagApiInformationFile.From(workspaceApiName, workspaceTagName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceTagApi(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceTagApiDto(builder);
        ConfigurePutWorkspaceTagApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagApi);
    }

    private static PutWorkspaceTagApi GetPutWorkspaceTagApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceTagApiDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceTagApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceTagApi));

            var dtoOption = await findDto(workspaceApiName, workspaceTagName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceApiName, dto, workspaceTagName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceTagApiDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceTagApiDto);
    }

    private static FindWorkspaceTagApiDto GetFindWorkspaceTagApiDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceTagApiInformationFile.From(workspaceApiName, workspaceTagName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceTagApiDto>();
        };
    }

    private static void ConfigurePutWorkspaceTagApiInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagApiInApim);
    }

    private static PutWorkspaceTagApiInApim GetPutWorkspaceTagApiInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, dto, workspaceTagName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting API {WorkspaceApiName} in tag {WorkspaceTagName} in workspace {WorkspaceName}...", workspaceApiName, workspaceTagName, workspaceName);

            var resourceUri = WorkspaceTagApiUri.From(workspaceApiName, workspaceTagName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceTagApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceTagApiName(builder);
        ConfigureIsWorkspaceTagApiNameInSourceControl(builder);
        ConfigureDeleteWorkspaceTagApi(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagApis);
    }

    private static DeleteWorkspaceTagApis GetDeleteWorkspaceTagApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceTagApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceTagApiNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceTagApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceTagApis));

            logger.LogInformation("Deleting workspace tag APIs...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiName, resource.WorkspaceTagName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceApiName, resource.WorkspaceTagName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceTagApi(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceTagApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagApi);
    }

    private static DeleteWorkspaceTagApi GetDeleteWorkspaceTagApi(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceTagApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceTagApi));

            await deleteFromApim(workspaceApiName, workspaceTagName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceTagApiFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagApiFromApim);
    }

    private static DeleteWorkspaceTagApiFromApim GetDeleteWorkspaceTagApiFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting API {WorkspaceApiName} in tag {WorkspaceTagName} in workspace {WorkspaceName}...", workspaceApiName, workspaceTagName, workspaceName);

            var resourceUri = WorkspaceTagApiUri.From(workspaceApiName, workspaceTagName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}