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

public delegate ValueTask PutProductTags(CancellationToken cancellationToken);
public delegate Option<(TagName Name, ProductName ProductName)> TryParseProductTagName(FileInfo file);
public delegate bool IsProductTagNameInSourceControl(TagName name, ProductName productName);
public delegate ValueTask PutProductTag(TagName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ProductTagDto>> FindProductTagDto(TagName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask PutProductTagInApim(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductTags(CancellationToken cancellationToken);
public delegate ValueTask DeleteProductTag(TagName name, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductTagFromApim(TagName name, ProductName productName, CancellationToken cancellationToken);

internal static class ProductTagModule
{
    public static void ConfigurePutProductTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryProductParseTagName(builder);
        ConfigureIsProductTagNameInSourceControl(builder);
        ConfigurePutProductTag(builder);

        builder.Services.TryAddSingleton(GetPutProductTags);
    }

    private static PutProductTags GetPutProductTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductTagNameInSourceControl>();
        var put = provider.GetRequiredService<PutProductTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductTags));

            logger.LogInformation("Putting product tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.ProductName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryProductParseTagName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseProductTagName);
    }

    private static TryParseProductTagName GetTryParseProductTagName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in ProductTagInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsProductTagNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsTagNameInSourceControl);
    }

    private static IsProductTagNameInSourceControl GetIsTagNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(TagName name, ProductName productName)
        {
            var artifactFiles = getArtifactFiles();
            var tagFile = ProductTagInformationFile.From(name, productName, serviceDirectory);

            return artifactFiles.Contains(tagFile.ToFileInfo());
        }
    }

    private static void ConfigurePutProductTag(IHostApplicationBuilder builder)
    {
        ConfigureFindProductTagDto(builder);
        ConfigurePutProductTagInApim(builder);

        builder.Services.TryAddSingleton(GetPutProductTag);
    }

    private static PutProductTag GetPutProductTag(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindProductTagDto>();
        var putInApim = provider.GetRequiredService<PutProductTagInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProductTag))
                                       ?.AddTag("product_tag.name", name)
                                       ?.AddTag("product.name", productName);

            var dtoOption = await findDto(name, productName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, productName, cancellationToken));
        };
    }

    private static void ConfigureFindProductTagDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindProductTagDto);
    }

    private static FindProductTagDto GetFindProductTagDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, productName, cancellationToken) =>
        {
            var informationFile = ProductTagInformationFile.From(name, productName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<ProductTagDto>();
        };
    }

    private static void ConfigurePutProductTagInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutProductTagInApim);
    }

    private static PutProductTagInApim GetPutProductTagInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            logger.LogInformation("Adding tag {TagName} to product {ProductName}...", name, productName);

            await ProductTagUri.From(name, productName, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteProductTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryProductParseTagName(builder);
        ConfigureIsProductTagNameInSourceControl(builder);
        ConfigureDeleteProductTag(builder);

        builder.Services.TryAddSingleton(GetDeleteProductTags);
    }

    private static DeleteProductTags GetDeleteProductTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductTagNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteProductTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductTags));

            logger.LogInformation("Deleting product tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.ProductName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductTag(IHostApplicationBuilder builder)
    {
        ConfigureDeleteProductTagFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteProductTag);
    }

    private static DeleteProductTag GetDeleteProductTag(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteProductTagFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProductTag))
                                       ?.AddTag("product_tag.name", name)
                                       ?.AddTag("product.name", productName);

            await deleteFromApim(name, productName, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductTagFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteProductTagFromApim);
    }

    private static DeleteProductTagFromApim GetDeleteProductTagFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, productName, cancellationToken) =>
        {
            logger.LogInformation("Removing tag {TagName} from product {ProductName}...", name, productName);

            await ProductTagUri.From(name, productName, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}