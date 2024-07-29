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

public delegate ValueTask PutProductGroups(CancellationToken cancellationToken);
public delegate Option<(GroupName Name, ProductName ProductName)> TryParseProductGroupName(FileInfo file);
public delegate bool IsProductGroupNameInSourceControl(GroupName name, ProductName productName);
public delegate ValueTask PutProductGroup(GroupName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ProductGroupDto>> FindProductGroupDto(GroupName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask PutProductGroupInApim(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductGroups(CancellationToken cancellationToken);
public delegate ValueTask DeleteProductGroup(GroupName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductGroupFromApim(GroupName name, ProductName productName, CancellationToken cancellationToken);

internal static class ProductGroupModule
{
    public static void ConfigurePutProductGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryProductParseGroupName(builder);
        ConfigureIsProductGroupNameInSourceControl(builder);
        ConfigurePutProductGroup(builder);

        builder.Services.TryAddSingleton(GetPutProductGroups);
    }

    private static PutProductGroups GetPutProductGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductGroupNameInSourceControl>();
        var put = provider.GetRequiredService<PutProductGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductGroups));

            logger.LogInformation("Putting product groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(group => isNameInSourceControl(group.Name, group.ProductName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryProductParseGroupName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseProductGroupName);
    }

    private static TryParseProductGroupName GetTryParseProductGroupName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in ProductGroupInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsProductGroupNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsGroupNameInSourceControl);
    }

    private static IsProductGroupNameInSourceControl GetIsGroupNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(GroupName name, ProductName productName)
        {
            var artifactFiles = getArtifactFiles();
            var groupFile = ProductGroupInformationFile.From(name, productName, serviceDirectory);

            return artifactFiles.Contains(groupFile.ToFileInfo());
        }
    }

    private static void ConfigurePutProductGroup(IHostApplicationBuilder builder)
    {
        ConfigureFindProductGroupDto(builder);
        ConfigurePutProductGroupInApim(builder);

        builder.Services.TryAddSingleton(GetPutProductGroup);
    }

    private static PutProductGroup GetPutProductGroup(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindProductGroupDto>();
        var putInApim = provider.GetRequiredService<PutProductGroupInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductGroup))
                                       ?.AddTag("product_group.name", name)
                                       ?.AddTag("product.name", productName);

            var dtoOption = await findDto(name, productName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, productName, cancellationToken));
        };
    }

    private static void ConfigureFindProductGroupDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindProductGroupDto);
    }

    private static FindProductGroupDto GetFindProductGroupDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, productName, cancellationToken) =>
        {
            var informationFile = ProductGroupInformationFile.From(name, productName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<ProductGroupDto>();
        };
    }

    private static void ConfigurePutProductGroupInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutProductGroupInApim);
    }

    private static PutProductGroupInApim GetPutProductGroupInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            logger.LogInformation("Adding group {GroupName} to product {ProductName}...", name, productName);

            await ProductGroupUri.From(name, productName, serviceUri)
                                 .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteProductGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryProductParseGroupName(builder);
        ConfigureIsProductGroupNameInSourceControl(builder);
        ConfigureDeleteProductGroup(builder);

        builder.Services.TryAddSingleton(GetDeleteProductGroups);
    }

    private static DeleteProductGroups GetDeleteProductGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductGroupNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteProductGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductGroups));

            logger.LogInformation("Deleting product groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(group => isNameInSourceControl(group.Name, group.ProductName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductGroup(IHostApplicationBuilder builder)
    {
        ConfigureDeleteProductGroupFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteProductGroup);
    }

    private static DeleteProductGroup GetDeleteProductGroup(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteProductGroupFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductGroup))
                                       ?.AddTag("product_group.name", name)
                                       ?.AddTag("product.name", productName);

            await deleteFromApim(name, productName, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductGroupFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteProductGroupFromApim);
    }

    private static DeleteProductGroupFromApim GetDeleteProductGroupFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, productName, cancellationToken) =>
        {
            logger.LogInformation("Removing group {GroupName} from product {ProductName}...", name, productName);

            await ProductGroupUri.From(name, productName, serviceUri)
                                 .Delete(pipeline, cancellationToken);
        };
    }
}