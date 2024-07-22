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

public delegate ValueTask PutWorkspaceProducts(CancellationToken cancellationToken);
public delegate Option<(ProductName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceProductName(FileInfo file);
public delegate bool IsWorkspaceProductNameInSourceControl(ProductName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceProduct(ProductName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceProductDto>> FindWorkspaceProductDto(ProductName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceProductInApim(ProductName name, WorkspaceProductDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProducts(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProduct(ProductName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductFromApim(ProductName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductModule
{
    public static void ConfigurePutWorkspaceProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseProductName(builder);
        ConfigureIsWorkspaceProductNameInSourceControl(builder);
        ConfigurePutWorkspaceProduct(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProducts);
    }

    private static PutWorkspaceProducts GetPutWorkspaceProducts(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceProduct>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProducts));

            logger.LogInformation("Putting workspace products...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(product => isNameInSourceControl(product.Name, product.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParseProductName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceProductName);
    }

    private static TryParseWorkspaceProductName GetTryParseWorkspaceProductName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceProductInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceProductNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsProductNameInSourceControl);
    }

    private static IsWorkspaceProductNameInSourceControl GetIsProductNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(ProductName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var productFile = WorkspaceProductInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(productFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceProduct(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceProductDto(builder);
        ConfigurePutWorkspaceProductInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProduct);
    }

    private static PutWorkspaceProduct GetPutWorkspaceProduct(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceProductDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceProductInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProduct))
                                       ?.AddTag("workspace_product.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceProductDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceProductDto);
    }

    private static FindWorkspaceProductDto GetFindWorkspaceProductDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductInformationFile.From(name, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceProductDto>();
        };
    }

    private static void ConfigurePutWorkspaceProductInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductInApim);
    }

    private static PutWorkspaceProductInApim GetPutWorkspaceProductInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Adding product {ProductName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceProductUri.From(name, workspaceName, serviceUri)
                                     .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseProductName(builder);
        ConfigureIsWorkspaceProductNameInSourceControl(builder);
        ConfigureDeleteWorkspaceProduct(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProducts);
    }

    private static DeleteWorkspaceProducts GetDeleteWorkspaceProducts(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceProduct>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProducts));

            logger.LogInformation("Deleting workspace products...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(product => isNameInSourceControl(product.Name, product.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProduct(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceProductFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProduct);
    }

    private static DeleteWorkspaceProduct GetDeleteWorkspaceProduct(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceProductFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProduct))
                                       ?.AddTag("workspace_product.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductFromApim);
    }

    private static DeleteWorkspaceProductFromApim GetDeleteWorkspaceProductFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing product {ProductName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceProductUri.From(name, workspaceName, serviceUri)
                                     .Delete(pipeline, cancellationToken);
        };
    }
}