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

internal delegate Option<PublisherAction> FindProductApiAction(FileInfo file);

file delegate Option<(ApiName Name, ProductName ProductName)> TryParseApiName(FileInfo file);

file delegate ValueTask ProcessProductApi(ApiName name, ProductName productName, CancellationToken cancellationToken);

file delegate bool IsApiNameInSourceControl(ApiName name, ProductName productName);

file delegate ValueTask<Option<ProductApiDto>> FindProductApiDto(ApiName name, ProductName productName, CancellationToken cancellationToken);

internal delegate ValueTask PutProductApi(ApiName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductApi(ApiName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask PutProductApiInApim(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductApiFromApim(ApiName name, ProductName productName, CancellationToken cancellationToken);

file sealed class FindProductApiActionHandler(TryParseApiName tryParseName, ProcessProductApi processProductApi)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ProductName);

    private PublisherAction GetAction(ApiName name, ProductName productName) =>
        async cancellationToken => await processProductApi(name, productName, cancellationToken);
}

file sealed class TryParseApiNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(ApiName, ProductName)> Handle(FileInfo file) =>
        TryParseNameFromApiInformationFile(file);

    private Option<(ApiName, ProductName)> TryParseNameFromApiInformationFile(FileInfo file) =>
        from informationFile in ProductApiInformationFile.TryParse(file, serviceDirectory)
        select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ProductApiSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(ApiName, ProductName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(ApiName, ProductName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ApiName name, ProductName productName, Func<ApiName, ProductName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessProductApiHandler(IsApiNameInSourceControl isNameInSourceControl, PutProductApi put, DeleteProductApi delete) : IDisposable
{
    private readonly ProductApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ApiName name, ProductName productName, CancellationToken cancellationToken)
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

file sealed class IsApiNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ApiName name, ProductName productName) =>
        DoesApiInformationFileExist(name, productName);

    private bool DoesApiInformationFileExist(ApiName name, ProductName productName)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = ProductApiInformationFile.From(name, productName, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutProductApiHandler(FindProductApiDto findDto, PutProduct putProduct, PutApi putApi, PutProductApiInApim putInApim) : IDisposable
{
    private readonly ProductApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Put, cancellationToken);

    private async ValueTask Put(ApiName name, ProductName productName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, productName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, productName, cancellationToken));
    }

    private async ValueTask Put(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await putProduct(productName, cancellationToken);
        await putApi(name, cancellationToken);
        await putInApim(name, dto, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindProductApiDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ProductApiDto>> Handle(ApiName name, ProductName productName, CancellationToken cancellationToken)
    {
        var informationFile = ProductApiInformationFile.From(name, productName, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<ProductApiDto>();
    }
}

file sealed class PutProductApiInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Adding api {ApiName} to product {ProductName}...", name, productName);
        await ProductApiUri.From(name, productName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteProductApiHandler(DeleteProductApiFromApim deleteFromApim) : IDisposable
{
    private readonly ProductApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Delete, cancellationToken);

    private async ValueTask Delete(ApiName name, ProductName productName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteProductApiFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting api {ApiName} from product {ProductName}...", name, productName);
        await ProductApiUri.From(name, productName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ProductApiServices
{
    public static void ConfigureFindProductApiAction(IServiceCollection services)
    {
        ConfigureTryParseApiName(services);
        ConfigureProcessProductApi(services);

        services.TryAddSingleton<FindProductApiActionHandler>();
        services.TryAddSingleton<FindProductApiAction>(provider => provider.GetRequiredService<FindProductApiActionHandler>().Handle);
    }

    private static void ConfigureTryParseApiName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseApiNameHandler>();
        services.TryAddSingleton<TryParseApiName>(provider => provider.GetRequiredService<TryParseApiNameHandler>().Handle);
    }

    private static void ConfigureProcessProductApi(IServiceCollection services)
    {
        ConfigureIsApiNameInSourceControl(services);
        ConfigurePutProductApi(services);
        ConfigureDeleteProductApi(services);

        services.TryAddSingleton<ProcessProductApiHandler>();
        services.TryAddSingleton<ProcessProductApi>(provider => provider.GetRequiredService<ProcessProductApiHandler>().Handle);
    }

    private static void ConfigureIsApiNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsApiNameInSourceControlHandler>();
        services.TryAddSingleton<IsApiNameInSourceControl>(provider => provider.GetRequiredService<IsApiNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutProductApi(IServiceCollection services)
    {
        ConfigureFindProductApiDto(services);
        ConfigurePutProductApiInApim(services);
        ProductServices.ConfigurePutProduct(services);
        ApiServices.ConfigurePutApi(services);

        services.TryAddSingleton<PutProductApiHandler>();
        services.TryAddSingleton<PutProductApi>(provider => provider.GetRequiredService<PutProductApiHandler>().Handle);
    }

    private static void ConfigureFindProductApiDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindProductApiDtoHandler>();
        services.TryAddSingleton<FindProductApiDto>(provider => provider.GetRequiredService<FindProductApiDtoHandler>().Handle);
    }

    private static void ConfigurePutProductApiInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutProductApiInApimHandler>();
        services.TryAddSingleton<PutProductApiInApim>(provider => provider.GetRequiredService<PutProductApiInApimHandler>().Handle);
    }

    private static void ConfigureDeleteProductApi(IServiceCollection services)
    {
        ConfigureDeleteProductApiFromApim(services);

        services.TryAddSingleton<DeleteProductApiHandler>();
        services.TryAddSingleton<DeleteProductApi>(provider => provider.GetRequiredService<DeleteProductApiHandler>().Handle);
    }

    private static void ConfigureDeleteProductApiFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteProductApiFromApimHandler>();
        services.TryAddSingleton<DeleteProductApiFromApim>(provider => provider.GetRequiredService<DeleteProductApiFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ProductApiPublisher");
}