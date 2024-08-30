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
public delegate ValueTask DeleteWorkspaceProducts(CancellationToken cancellationToken);
public delegate Option<(WorkspaceProductName WorkspaceProductName, WorkspaceName WorkspaceName)> TryParseWorkspaceProductName(FileInfo file);
public delegate bool IsWorkspaceProductNameInSourceControl(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceProduct(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceProductDto>> FindWorkspaceProductDto(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceProductInApim(WorkspaceProductName name, WorkspaceProductDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProduct(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductFromApim(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductModule
{
    public static void ConfigurePutWorkspaceProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductName(builder);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceProductName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceProductName(IHostApplicationBuilder builder)
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

        builder.Services.TryAddSingleton(GetIsWorkspaceProductNameInSourceControl);
    }

    private static IsWorkspaceProductNameInSourceControl GetIsWorkspaceProductNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceProductInformationFile.From(workspaceProductName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
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

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProduct));

            var dtoOption = await findDto(workspaceProductName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceProductName, dto, workspaceName, cancellationToken));
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

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceProductInformationFile.From(workspaceProductName, workspaceName, serviceDirectory);
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

        return async (workspaceProductName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductName, workspaceName);

            var productAlreadyExists = await doesProductAlreadyExist(workspaceProductName, workspaceName, cancellationToken);

            var resourceUri = WorkspaceProductUri.From(workspaceProductName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);

            // Delete automatically created resources if the product is new. This ensures that APIM is consistent with source control.
            if (productAlreadyExists is false)
            {
                await deleteAutomaticallyCreatedProductGroups(workspaceProductName, workspaceName, cancellationToken);
                await deleteAutomaticallyCreatedProductSubscriptions(workspaceProductName, workspaceName, cancellationToken);
            }
        };

        async ValueTask<bool> doesProductAlreadyExist(WorkspaceProductName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var productUri = WorkspaceProductUri.From(name, workspaceName, serviceUri).ToUri();
            var contentOption = await pipeline.GetContentOption(productUri, cancellationToken);
            return contentOption.IsSome;
        }

        async ValueTask deleteAutomaticallyCreatedProductGroups(WorkspaceProductName productName, WorkspaceName workspaceName, CancellationToken cancellationToken) =>
            await WorkspaceProductGroupsUri
                    .From(productName, workspaceName, serviceUri)
                    .ListNames(pipeline, cancellationToken)
                    .Do(groupName => logger.LogWarning("Removing automatically added group {WorkspaceGroupName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", groupName, productName, workspaceName))
                    .IterParallel(async name => await WorkspaceProductGroupUri.From(name, productName, workspaceName, serviceUri)
                                                                              .Delete(pipeline, cancellationToken),
                                  cancellationToken);

        async ValueTask deleteAutomaticallyCreatedProductSubscriptions(WorkspaceProductName productName, WorkspaceName workspaceName, CancellationToken cancellationToken) =>
            await WorkspaceSubscriptionsUri.From(workspaceName, serviceUri)
                                           .List(pipeline, cancellationToken)
                                           .Choose(subscription => from name in common.WorkspaceSubscriptionModule.TryGetProductName(subscription.Dto)
                                                                   where name == productName
                                                                   select subscription.Name)
                                           .Do(subscriptionName => logger.LogWarning("Removing automatically created subscription {WorkspaceSubscriptionName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", subscriptionName, productName, workspaceName))
                                           .IterParallel(async name => await WorkspaceSubscriptionUri.From(name, workspaceName, serviceUri)
                                                                                                     .Delete(pipeline, cancellationToken),
                                                         cancellationToken);
    }

    public static void ConfigureDeleteWorkspaceProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductName(builder);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceProductName, resource.WorkspaceName) is false)
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

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProduct));

            await deleteFromApim(workspaceProductName, workspaceName, cancellationToken);
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

        return async (workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductUri.From(workspaceProductName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}