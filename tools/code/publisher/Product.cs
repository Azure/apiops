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

public delegate ValueTask PutProducts(CancellationToken cancellationToken);
public delegate Option<ProductName> TryParseProductName(FileInfo file);
public delegate bool IsProductNameInSourceControl(ProductName name);
public delegate ValueTask PutProduct(ProductName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<ProductDto>> FindProductDto(ProductName name, CancellationToken cancellationToken);
public delegate ValueTask PutProductInApim(ProductName name, ProductDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteProducts(CancellationToken cancellationToken);
public delegate ValueTask DeleteProduct(ProductName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteProductFromApim(ProductName name, CancellationToken cancellationToken);

internal static class ProductModule
{
    public static void ConfigurePutProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseProductName(builder);
        ConfigureIsProductNameInSourceControl(builder);
        ConfigurePutProduct(builder);

        builder.Services.TryAddSingleton(GetPutProducts);
    }

    private static PutProducts GetPutProducts(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductNameInSourceControl>();
        var put = provider.GetRequiredService<PutProduct>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProducts));

            logger.LogInformation("Putting products...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseProductName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseProductName);
    }

    private static TryParseProductName GetTryParseProductName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in ProductInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsProductNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsProductNameInSourceControl);
    }

    private static IsProductNameInSourceControl GetIsProductNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(ProductName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = ProductInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutProduct(IHostApplicationBuilder builder)
    {
        ConfigureFindProductDto(builder);
        ConfigurePutProductInApim(builder);

        builder.Services.TryAddSingleton(GetPutProduct);
    }

    private static PutProduct GetPutProduct(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindProductDto>();
        var putInApim = provider.GetRequiredService<PutProductInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutProduct))
                                       ?.AddTag("product.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindProductDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindProductDto);
    }

    private static FindProductDto GetFindProductDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<ProductName, ProductDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = ProductInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<ProductDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutProductInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutProductInApim);
    }

    private static PutProductInApim GetPutProductInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting product {ProductName}...", name);

            var productAlreadyExists = await doesProductAlreadyExist(name, cancellationToken);
            await ProductUri.From(name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);

            // Delete automatically created resources if the product is new. This ensures that APIM is consistent with source control.
            if (productAlreadyExists is false)
            {
                await deleteAutomaticallyCreatedProductGroups(name, cancellationToken);
                await deleteAutomaticallyCreatedProductSubscriptions(name, cancellationToken);
            }
        };

        async ValueTask<bool> doesProductAlreadyExist(ProductName name, CancellationToken cancellationToken)
        {
            var productUri = ProductUri.From(name, serviceUri).ToUri();
            var contentOption = await pipeline.GetContentOption(productUri, cancellationToken);
            return contentOption.IsSome;
        }

        async ValueTask deleteAutomaticallyCreatedProductGroups(ProductName productName, CancellationToken cancellationToken) =>
            await ProductGroupsUri.From(productName, serviceUri)
                                  .ListNames(pipeline, cancellationToken)
                                  .Do(groupName => logger.LogWarning("Removing automatically added group {GroupName} from product {ProductName}...", groupName, productName))
                                  .IterParallel(async name => await ProductGroupUri.From(name, productName, serviceUri)
                                                                                   .Delete(pipeline, cancellationToken),
                                                cancellationToken);

        async ValueTask deleteAutomaticallyCreatedProductSubscriptions(ProductName productName, CancellationToken cancellationToken) =>
            await ProductUri.From(productName, serviceUri)
                            .ListSubscriptionNames(pipeline, cancellationToken)
                            .WhereAwait(async subscriptionName =>
                            {
                                var dto = await SubscriptionUri.From(subscriptionName, serviceUri)
                                                               .GetDto(pipeline, cancellationToken);

                                // Automatically created subscriptions have no display name and end with "/users/1"
                                return string.IsNullOrWhiteSpace(dto.Properties.DisplayName)
                                       && (dto.Properties.OwnerId?.EndsWith("/users/1", StringComparison.OrdinalIgnoreCase) ?? false);
                            })
                            .Do(subscriptionName => logger.LogWarning("Deleting automatically created subscription {SubscriptionName} from product {ProductName}...", subscriptionName, productName))
                            .IterParallel(async name => await SubscriptionUri.From(name, serviceUri)
                                                                             .Delete(pipeline, cancellationToken),
                                        cancellationToken);
    }

    public static void ConfigureDeleteProducts(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseProductName(builder);
        ConfigureIsProductNameInSourceControl(builder);
        ConfigureDeleteProduct(builder);

        builder.Services.TryAddSingleton(GetDeleteProducts);
    }

    private static DeleteProducts GetDeleteProducts(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseProductName>();
        var isNameInSourceControl = provider.GetRequiredService<IsProductNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteProduct>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProducts));

            logger.LogInformation("Deleting products...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteProduct(IHostApplicationBuilder builder)
    {
        ConfigureDeleteProductFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteProduct);
    }

    private static DeleteProduct GetDeleteProduct(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteProductFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteProduct))
                                       ?.AddTag("product.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteProductFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteProductFromApim);
    }

    private static DeleteProductFromApim GetDeleteProductFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting product {ProductName}...", name);

            await ProductUri.From(name, serviceUri)
                            .Delete(pipeline, cancellationToken);
        };
    }
}