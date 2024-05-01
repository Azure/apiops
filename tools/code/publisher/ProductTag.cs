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

internal delegate Option<PublisherAction> FindProductTagAction(FileInfo file);

file delegate Option<(TagName Name, ProductName ProductName)> TryParseTagName(FileInfo file);

file delegate ValueTask ProcessProductTag(TagName name, ProductName productName, CancellationToken cancellationToken);

file delegate bool IsTagNameInSourceControl(TagName name, ProductName productName);

file delegate ValueTask<Option<ProductTagDto>> FindProductTagDto(TagName name, ProductName productName, CancellationToken cancellationToken);

internal delegate ValueTask PutProductTag(TagName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductTag(TagName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask PutProductTagInApim(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductTagFromApim(TagName name, ProductName productName, CancellationToken cancellationToken);

file sealed class FindProductTagActionHandler(TryParseTagName tryParseName, ProcessProductTag processProductTag)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ProductName);

    private PublisherAction GetAction(TagName name, ProductName productName) =>
        async cancellationToken => await processProductTag(name, productName, cancellationToken);
}

file sealed class TryParseTagNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(TagName, ProductName)> Handle(FileInfo file) =>
        TryParseNameFromTagInformationFile(file);

    private Option<(TagName, ProductName)> TryParseNameFromTagInformationFile(FileInfo file) =>
        from informationFile in ProductTagInformationFile.TryParse(file, serviceDirectory)
        select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ProductTagSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(TagName, ProductName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(TagName, ProductName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(TagName name, ProductName productName, Func<TagName, ProductName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessProductTagHandler(IsTagNameInSourceControl isNameInSourceControl, PutProductTag put, DeleteProductTag delete) : IDisposable
{
    private readonly ProductTagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(TagName name, ProductName productName, CancellationToken cancellationToken)
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

file sealed class IsTagNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(TagName name, ProductName productName) =>
        DoesTagInformationFileExist(name, productName);

    private bool DoesTagInformationFileExist(TagName name, ProductName productName)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = ProductTagInformationFile.From(name, productName, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutProductTagHandler(FindProductTagDto findDto, PutProduct putProduct, PutTag putTag, PutProductTagInApim putInApim) : IDisposable
{
    private readonly ProductTagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Put, cancellationToken);

    private async ValueTask Put(TagName name, ProductName productName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, productName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, productName, cancellationToken));
    }

    private async ValueTask Put(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await putProduct(productName, cancellationToken);
        await putTag(name, cancellationToken);
        await putInApim(name, dto, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindProductTagDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ProductTagDto>> Handle(TagName name, ProductName productName, CancellationToken cancellationToken)
    {
        var informationFile = ProductTagInformationFile.From(name, productName, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<ProductTagDto>();
    }
}

file sealed class PutProductTagInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Adding tag {TagName} to product {ProductName}...", name, productName);
        await ProductTagUri.From(name, productName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteProductTagHandler(DeleteProductTagFromApim deleteFromApim) : IDisposable
{
    private readonly ProductTagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Delete, cancellationToken);

    private async ValueTask Delete(TagName name, ProductName productName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteProductTagFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting tag {TagName} from product {ProductName}...", name, productName);
        await ProductTagUri.From(name, productName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ProductTagServices
{
    public static void ConfigureFindProductTagAction(IServiceCollection services)
    {
        ConfigureTryParseTagName(services);
        ConfigureProcessProductTag(services);

        services.TryAddSingleton<FindProductTagActionHandler>();
        services.TryAddSingleton<FindProductTagAction>(provider => provider.GetRequiredService<FindProductTagActionHandler>().Handle);
    }

    private static void ConfigureTryParseTagName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseTagNameHandler>();
        services.TryAddSingleton<TryParseTagName>(provider => provider.GetRequiredService<TryParseTagNameHandler>().Handle);
    }

    private static void ConfigureProcessProductTag(IServiceCollection services)
    {
        ConfigureIsTagNameInSourceControl(services);
        ConfigurePutProductTag(services);
        ConfigureDeleteProductTag(services);

        services.TryAddSingleton<ProcessProductTagHandler>();
        services.TryAddSingleton<ProcessProductTag>(provider => provider.GetRequiredService<ProcessProductTagHandler>().Handle);
    }

    private static void ConfigureIsTagNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsTagNameInSourceControlHandler>();
        services.TryAddSingleton<IsTagNameInSourceControl>(provider => provider.GetRequiredService<IsTagNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutProductTag(IServiceCollection services)
    {
        ConfigureFindProductTagDto(services);
        ConfigurePutProductTagInApim(services);
        ProductServices.ConfigurePutProduct(services);
        TagServices.ConfigurePutTag(services);

        services.TryAddSingleton<PutProductTagHandler>();
        services.TryAddSingleton<PutProductTag>(provider => provider.GetRequiredService<PutProductTagHandler>().Handle);
    }

    private static void ConfigureFindProductTagDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindProductTagDtoHandler>();
        services.TryAddSingleton<FindProductTagDto>(provider => provider.GetRequiredService<FindProductTagDtoHandler>().Handle);
    }

    private static void ConfigurePutProductTagInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutProductTagInApimHandler>();
        services.TryAddSingleton<PutProductTagInApim>(provider => provider.GetRequiredService<PutProductTagInApimHandler>().Handle);
    }

    private static void ConfigureDeleteProductTag(IServiceCollection services)
    {
        ConfigureDeleteProductTagFromApim(services);

        services.TryAddSingleton<DeleteProductTagHandler>();
        services.TryAddSingleton<DeleteProductTag>(provider => provider.GetRequiredService<DeleteProductTagHandler>().Handle);
    }

    private static void ConfigureDeleteProductTagFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteProductTagFromApimHandler>();
        services.TryAddSingleton<DeleteProductTagFromApim>(provider => provider.GetRequiredService<DeleteProductTagFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ProductTagPublisher");
}