using AsyncKeyedLock;
using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate Option<PublisherAction> FindProductAction(FileInfo file);

file delegate Option<ProductName> TryParseProductName(FileInfo file);

file delegate ValueTask ProcessProduct(ProductName name, CancellationToken cancellationToken);

file delegate bool IsProductNameInSourceControl(ProductName name);

file delegate ValueTask<Option<ProductDto>> FindProductDto(ProductName name, CancellationToken cancellationToken);

internal delegate ValueTask PutProduct(ProductName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteProduct(ProductName name, CancellationToken cancellationToken);

file delegate ValueTask PutProductInApim(ProductName name, ProductDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductFromApim(ProductName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingProduct(ProductName name, CancellationToken cancellationToken);

file sealed class FindProductActionHandler(TryParseProductName tryParseName, ProcessProduct processProduct)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(ProductName name) =>
        async cancellationToken => await processProduct(name, cancellationToken);
}

file sealed class TryParseProductNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<ProductName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<ProductName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in ProductInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ProductSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<ProductName> locker = new();
    private ImmutableHashSet<ProductName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ProductName name, Func<ProductName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync(name, cancellationToken);

        // Only process each name once
        if (processedNames.Contains(name))
        {
            return;
        }

        await action(name, cancellationToken);

        ImmutableInterlocked.Update(ref processedNames, set => set.Add(name));
    }

    public void Dispose() => locker.Dispose();
}

file sealed class ProcessProductHandler(IsProductNameInSourceControl isNameInSourceControl, PutProduct put, DeleteProduct delete) : IDisposable
{
    private readonly ProductSemaphore semaphore = new();

    public async ValueTask Handle(ProductName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ProductName name, CancellationToken cancellationToken)
    {
        if (isNameInSourceControl(name))
        {
            await put(name, cancellationToken);
        }
        else
        {
            await delete(name, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class IsProductNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ProductName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(ProductName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = ProductInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutProductHandler(FindProductDto findDto, PutProductInApim putInApim) : IDisposable
{
    private readonly ProductSemaphore semaphore = new();

    public async ValueTask Handle(ProductName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(ProductName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindProductDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<ProductDto>> Handle(ProductName name, CancellationToken cancellationToken)
    {
        var informationFile = ProductInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<ProductDto>()
               let overrideDto = overrideFactory.Create<ProductName, ProductDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutProductInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ProductName name, ProductDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting product {ProductName}...", name);

        var productAlreadyExists = await DoesProductAlreadyExist(name, cancellationToken);
        await ProductUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);

        // Delete automatically created resources if the product is new. This ensures that APIM is consistent with source control.
        if (productAlreadyExists is false)
        {
            await DeleteAutomaticallyCreatedProductGroups(name, cancellationToken);
            await DeleteAutomaticallyCreatedProductSubscriptions(name, cancellationToken);
        }
    }

    private async ValueTask<bool> DoesProductAlreadyExist(ProductName name, CancellationToken cancellationToken)
    {
        var productDtoOption = await ProductUri.From(name, serviceUri).TryGetDto(pipeline, cancellationToken);
        return productDtoOption.IsSome;
    }

    private async ValueTask DeleteAutomaticallyCreatedProductGroups(ProductName productName, CancellationToken cancellationToken) =>
        await ProductGroupsUri.From(productName, serviceUri)
                              .ListNames(pipeline, cancellationToken)
                              .Do(groupName => logger.LogWarning("Removing automatically added {GroupName} from product {ProductName}...", groupName, productName))
                              .IterParallel(async name => await ProductGroupUri.From(name, productName, serviceUri)
                                                                               .Delete(pipeline, cancellationToken),
                                            cancellationToken);

    private async ValueTask DeleteAutomaticallyCreatedProductSubscriptions(ProductName productName, CancellationToken cancellationToken) =>
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

file sealed class DeleteProductHandler(IEnumerable<OnDeletingProduct> onDeletingHandlers, DeleteProductFromApim deleteFromApim) : IDisposable
{
    private readonly ProductSemaphore semaphore = new();

    public async ValueTask Handle(ProductName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(ProductName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteProductFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ProductName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting product {ProductName}...", name);
        await ProductUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ProductServices
{
    public static void ConfigureFindProductAction(IServiceCollection services)
    {
        ConfigureTryParseProductName(services);
        ConfigureProcessProduct(services);

        services.TryAddSingleton<FindProductActionHandler>();
        services.TryAddSingleton<FindProductAction>(provider => provider.GetRequiredService<FindProductActionHandler>().Handle);
    }

    private static void ConfigureTryParseProductName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseProductNameHandler>();
        services.TryAddSingleton<TryParseProductName>(provider => provider.GetRequiredService<TryParseProductNameHandler>().Handle);
    }

    private static void ConfigureProcessProduct(IServiceCollection services)
    {
        ConfigureIsProductNameInSourceControl(services);
        ConfigurePutProduct(services);
        ConfigureDeleteProduct(services);

        services.TryAddSingleton<ProcessProductHandler>();
        services.TryAddSingleton<ProcessProduct>(provider => provider.GetRequiredService<ProcessProductHandler>().Handle);
    }

    private static void ConfigureIsProductNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsProductNameInSourceControlHandler>();
        services.TryAddSingleton<IsProductNameInSourceControl>(provider => provider.GetRequiredService<IsProductNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutProduct(IServiceCollection services)
    {
        ConfigureFindProductDto(services);
        ConfigurePutProductInApim(services);

        services.TryAddSingleton<PutProductHandler>();
        services.TryAddSingleton<PutProduct>(provider => provider.GetRequiredService<PutProductHandler>().Handle);
    }

    private static void ConfigureFindProductDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindProductDtoHandler>();
        services.TryAddSingleton<FindProductDto>(provider => provider.GetRequiredService<FindProductDtoHandler>().Handle);
    }

    private static void ConfigurePutProductInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutProductInApimHandler>();
        services.TryAddSingleton<PutProductInApim>(provider => provider.GetRequiredService<PutProductInApimHandler>().Handle);
    }

    private static void ConfigureDeleteProduct(IServiceCollection services)
    {
        ConfigureOnDeletingProduct(services);
        ConfigureDeleteProductFromApim(services);

        services.TryAddSingleton<DeleteProductHandler>();
        services.TryAddSingleton<DeleteProduct>(provider => provider.GetRequiredService<DeleteProductHandler>().Handle);
    }

    private static void ConfigureOnDeletingProduct(IServiceCollection services)
    {
        SubscriptionServices.ConfigureOnDeletingProduct(services);
    }

    private static void ConfigureDeleteProductFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteProductFromApimHandler>();
        services.TryAddSingleton<DeleteProductFromApim>(provider => provider.GetRequiredService<DeleteProductFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ProductPublisher");
}