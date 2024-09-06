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

public delegate ValueTask PutWorkspaceTagProducts(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagProducts(CancellationToken cancellationToken);
public delegate Option<(WorkspaceProductName WorkspaceProductName, WorkspaceTagName WorkspaceTagName, WorkspaceName WorkspaceName)> TryParseWorkspaceTagProductName(FileInfo file);
public delegate bool IsWorkspaceTagProductNameInSourceControl(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceTagProduct(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceTagProductDto>> FindWorkspaceTagProductDto(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceTagProductInApim(WorkspaceProductName name, WorkspaceTagProductDto dto, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagProduct(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceTagProductFromApim(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceTagProductModule
{
    public static void ConfigurePutWorkspaceTagProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceTagProductName(builder);
        ConfigureIsWorkspaceTagProductNameInSourceControl(builder);
        ConfigurePutWorkspaceTagProduct(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagProducts);
    }

    private static PutWorkspaceTagProducts GetPutWorkspaceTagProducts(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceTagProductName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceTagProductNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceTagProduct>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceTagProducts));

            logger.LogInformation("Putting workspace tag products...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceProductName, resource.WorkspaceTagName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceProductName, resource.WorkspaceTagName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceTagProductName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceTagProductName);
    }

    private static TryParseWorkspaceTagProductName GetTryParseWorkspaceTagProductName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceTagProductInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name, informationFile.Parent.Parent.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceTagProductNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceTagProductNameInSourceControl);
    }

    private static IsWorkspaceTagProductNameInSourceControl GetIsWorkspaceTagProductNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceTagProductInformationFile.From(workspaceProductName, workspaceTagName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceTagProduct(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceTagProductDto(builder);
        ConfigurePutWorkspaceTagProductInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagProduct);
    }

    private static PutWorkspaceTagProduct GetPutWorkspaceTagProduct(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceTagProductDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceTagProductInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceProductName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceTagProduct));

            var dtoOption = await findDto(workspaceProductName, workspaceTagName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceProductName, dto, workspaceTagName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceTagProductDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceTagProductDto);
    }

    private static FindWorkspaceTagProductDto GetFindWorkspaceTagProductDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceProductName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceTagProductInformationFile.From(workspaceProductName, workspaceTagName, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceTagProductDto>();
        };
    }

    private static void ConfigurePutWorkspaceTagProductInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceTagProductInApim);
    }

    private static PutWorkspaceTagProductInApim GetPutWorkspaceTagProductInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductName, dto, workspaceTagName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting product {WorkspaceProductName} in tag {WorkspaceTagName} in workspace {WorkspaceName}...", workspaceProductName, workspaceTagName, workspaceName);

            var resourceUri = WorkspaceTagProductUri.From(workspaceProductName, workspaceTagName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceTagProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceTagProductName(builder);
        ConfigureIsWorkspaceTagProductNameInSourceControl(builder);
        ConfigureDeleteWorkspaceTagProduct(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagProducts);
    }

    private static DeleteWorkspaceTagProducts GetDeleteWorkspaceTagProducts(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceTagProductName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceTagProductNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceTagProduct>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceTagProducts));

            logger.LogInformation("Deleting workspace tag products...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceProductName, resource.WorkspaceTagName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceProductName, resource.WorkspaceTagName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceTagProduct(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceTagProductFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagProduct);
    }

    private static DeleteWorkspaceTagProduct GetDeleteWorkspaceTagProduct(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceTagProductFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceProductName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceTagProduct));

            await deleteFromApim(workspaceProductName, workspaceTagName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceTagProductFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceTagProductFromApim);
    }

    private static DeleteWorkspaceTagProductFromApim GetDeleteWorkspaceTagProductFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductName, workspaceTagName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting product {WorkspaceProductName} in tag {WorkspaceTagName} in workspace {WorkspaceName}...", workspaceProductName, workspaceTagName, workspaceName);

            var resourceUri = WorkspaceTagProductUri.From(workspaceProductName, workspaceTagName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}