using AsyncKeyedLock;
using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate Option<PublisherAction> FindProductPolicyAction(FileInfo file);

file delegate Option<(ProductPolicyName Name, ProductName ProductName)> TryParseProductPolicyName(FileInfo file);

file delegate ValueTask ProcessProductPolicy(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);

file delegate bool IsProductPolicyNameInSourceControl(ProductPolicyName name, ProductName productName);

file delegate ValueTask<Option<ProductPolicyDto>> FindProductPolicyDto(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);

internal delegate ValueTask PutProductPolicy(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductPolicy(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask PutProductPolicyInApim(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductPolicyFromApim(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken);

file sealed class FindProductPolicyActionHandler(TryParseProductPolicyName tryParseName, ProcessProductPolicy processProductPolicy)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ProductName);

    private PublisherAction GetAction(ProductPolicyName name, ProductName productName) =>
        async cancellationToken => await processProductPolicy(name, productName, cancellationToken);
}

file sealed class TryParseProductPolicyNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(ProductPolicyName, ProductName)> Handle(FileInfo file) =>
        TryParseNameFromPolicyFile(file);

    private Option<(ProductPolicyName, ProductName)> TryParseNameFromPolicyFile(FileInfo file) =>
        from policyFile in ProductPolicyFile.TryParse(file, serviceDirectory)
        select (policyFile.Name, policyFile.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ProductPolicySemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(ProductPolicyName, ProductName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(ProductPolicyName, ProductName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ProductPolicyName name, ProductName productName, Func<ProductPolicyName, ProductName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync((name, productName), cancellationToken).ConfigureAwait(false);

        // Only process each name once
        if (processedNames.Contains((name, productName)))
        {
            return;
        }

        await action(name, productName, cancellationToken);

        ImmutableInterlocked.Update(ref processedNames, set => set.Add((name, productName)));
    }

    public void Dispose() => locker.Dispose();
}

file sealed class ProcessProductPolicyHandler(IsProductPolicyNameInSourceControl isNameInSourceControl, PutProductPolicy put, DeleteProductPolicy delete) : IDisposable
{
    private readonly ProductPolicySemaphore semaphore = new();

    public async ValueTask Handle(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
    {
        if (isNameInSourceControl(name, productName))
        {
            await put(name, productName, cancellationToken);
        }
        else
        {
            await delete(name, productName, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class IsProductPolicyNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ProductPolicyName name, ProductName productName) =>
        DoesPolicyFileExist(name, productName);

    private bool DoesPolicyFileExist(ProductPolicyName name, ProductName productName)
    {
        var artifactFiles = getArtifactFiles();
        var policyFile = ProductPolicyFile.From(name, productName, serviceDirectory);

        return artifactFiles.Contains(policyFile.ToFileInfo());
    }
}

file sealed class PutProductPolicyHandler(FindProductPolicyDto findDto, PutProduct putProduct, PutProductPolicyInApim putInApim) : IDisposable
{
    private readonly ProductPolicySemaphore semaphore = new();

    public async ValueTask Handle(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Put, cancellationToken);

    private async ValueTask Put(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, productName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, productName, cancellationToken));
    }

    private async ValueTask Put(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await putProduct(productName, cancellationToken);
        await putInApim(name, dto, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindProductPolicyDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ProductPolicyDto>> Handle(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
    {
        var contentsOption = await TryGetPolicyContents(name, productName, cancellationToken);

        return from contents in contentsOption
               select new ProductPolicyDto
               {
                   Properties = new ProductPolicyDto.ProductPolicyContract
                   {
                       Format = "rawxml",
                       Value = contents.ToString()
                   }
               };
    }

    private async ValueTask<Option<BinaryData>> TryGetPolicyContents(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
    {
        var policyFile = ProductPolicyFile.From(name, productName, serviceDirectory);

        return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
    }
}

file sealed class PutProductPolicyInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy {ProductPolicyName} for product {ProductName}...", name, productName);
        await ProductPolicyUri.From(name, productName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteProductPolicyHandler(DeleteProductPolicyFromApim deleteFromApim) : IDisposable
{
    private readonly ProductPolicySemaphore semaphore = new();

    public async ValueTask Handle(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Delete, cancellationToken);

    private async ValueTask Delete(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteProductPolicyFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ProductPolicyName name, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy {ProductPolicyName} from product {ProductName}...", name, productName);
        await ProductPolicyUri.From(name, productName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ProductPolicyServices
{
    public static void ConfigureFindProductPolicyAction(IServiceCollection services)
    {
        ConfigureTryParseProductPolicyName(services);
        ConfigureProcessProductPolicy(services);

        services.TryAddSingleton<FindProductPolicyActionHandler>();
        services.TryAddSingleton<FindProductPolicyAction>(provider => provider.GetRequiredService<FindProductPolicyActionHandler>().Handle);
    }

    private static void ConfigureTryParseProductPolicyName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseProductPolicyNameHandler>();
        services.TryAddSingleton<TryParseProductPolicyName>(provider => provider.GetRequiredService<TryParseProductPolicyNameHandler>().Handle);
    }

    private static void ConfigureProcessProductPolicy(IServiceCollection services)
    {
        ConfigureIsProductPolicyNameInSourceControl(services);
        ConfigurePutProductPolicy(services);
        ConfigureDeleteProductPolicy(services);

        services.TryAddSingleton<ProcessProductPolicyHandler>();
        services.TryAddSingleton<ProcessProductPolicy>(provider => provider.GetRequiredService<ProcessProductPolicyHandler>().Handle);
    }

    private static void ConfigureIsProductPolicyNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsProductPolicyNameInSourceControlHandler>();
        services.TryAddSingleton<IsProductPolicyNameInSourceControl>(provider => provider.GetRequiredService<IsProductPolicyNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutProductPolicy(IServiceCollection services)
    {
        ConfigureFindProductPolicyDto(services);
        ConfigurePutProductPolicyInApim(services);
        ProductServices.ConfigurePutProduct(services);

        services.TryAddSingleton<PutProductPolicyHandler>();
        services.TryAddSingleton<PutProductPolicy>(provider => provider.GetRequiredService<PutProductPolicyHandler>().Handle);
    }

    private static void ConfigureFindProductPolicyDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindProductPolicyDtoHandler>();
        services.TryAddSingleton<FindProductPolicyDto>(provider => provider.GetRequiredService<FindProductPolicyDtoHandler>().Handle);
    }

    private static void ConfigurePutProductPolicyInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutProductPolicyInApimHandler>();
        services.TryAddSingleton<PutProductPolicyInApim>(provider => provider.GetRequiredService<PutProductPolicyInApimHandler>().Handle);
    }

    private static void ConfigureDeleteProductPolicy(IServiceCollection services)
    {
        ConfigureDeleteProductPolicyFromApim(services);

        services.TryAddSingleton<DeleteProductPolicyHandler>();
        services.TryAddSingleton<DeleteProductPolicy>(provider => provider.GetRequiredService<DeleteProductPolicyHandler>().Handle);
    }

    private static void ConfigureDeleteProductPolicyFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteProductPolicyFromApimHandler>();
        services.TryAddSingleton<DeleteProductPolicyFromApim>(provider => provider.GetRequiredService<DeleteProductPolicyFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ProductPolicyPublisher");
}