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

internal delegate Option<PublisherAction> FindProductGroupAction(FileInfo file);

file delegate Option<(GroupName Name, ProductName ProductName)> TryParseGroupName(FileInfo file);

file delegate ValueTask ProcessProductGroup(GroupName name, ProductName productName, CancellationToken cancellationToken);

file delegate bool IsGroupNameInSourceControl(GroupName name, ProductName productName);

file delegate ValueTask<Option<ProductGroupDto>> FindProductGroupDto(GroupName name, ProductName productName, CancellationToken cancellationToken);

internal delegate ValueTask PutProductGroup(GroupName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductGroup(GroupName name, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask PutProductGroupInApim(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask DeleteProductGroupFromApim(GroupName name, ProductName productName, CancellationToken cancellationToken);

file sealed class FindProductGroupActionHandler(TryParseGroupName tryParseName, ProcessProductGroup processProductGroup)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ProductName);

    private PublisherAction GetAction(GroupName name, ProductName productName) =>
        async cancellationToken => await processProductGroup(name, productName, cancellationToken);
}

file sealed class TryParseGroupNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(GroupName, ProductName)> Handle(FileInfo file) =>
        TryParseNameFromGroupInformationFile(file);

    private Option<(GroupName, ProductName)> TryParseNameFromGroupInformationFile(FileInfo file) =>
        from informationFile in ProductGroupInformationFile.TryParse(file, serviceDirectory)
        select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ProductGroupSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(GroupName, ProductName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(GroupName, ProductName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(GroupName name, ProductName productName, Func<GroupName, ProductName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessProductGroupHandler(IsGroupNameInSourceControl isNameInSourceControl, PutProductGroup put, DeleteProductGroup delete) : IDisposable
{
    private readonly ProductGroupSemaphore semaphore = new();

    public async ValueTask Handle(GroupName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(GroupName name, ProductName productName, CancellationToken cancellationToken)
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

file sealed class IsGroupNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(GroupName name, ProductName productName) =>
        DoesGroupInformationFileExist(name, productName);

    private bool DoesGroupInformationFileExist(GroupName name, ProductName productName)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = ProductGroupInformationFile.From(name, productName, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutProductGroupHandler(FindProductGroupDto findDto, PutProduct putProduct, PutGroup putGroup, PutProductGroupInApim putInApim) : IDisposable
{
    private readonly ProductGroupSemaphore semaphore = new();

    public async ValueTask Handle(GroupName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Put, cancellationToken);

    private async ValueTask Put(GroupName name, ProductName productName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, productName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, productName, cancellationToken));
    }

    private async ValueTask Put(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await putProduct(productName, cancellationToken);
        await putGroup(name, cancellationToken);
        await putInApim(name, dto, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindProductGroupDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ProductGroupDto>> Handle(GroupName name, ProductName productName, CancellationToken cancellationToken)
    {
        var informationFile = ProductGroupInformationFile.From(name, productName, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<ProductGroupDto>();
    }
}

file sealed class PutProductGroupInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Adding group {GroupName} to product {ProductName}...", name, productName);
        await ProductGroupUri.From(name, productName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteProductGroupHandler(DeleteProductGroupFromApim deleteFromApim) : IDisposable
{
    private readonly ProductGroupSemaphore semaphore = new();

    public async ValueTask Handle(GroupName name, ProductName productName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, productName, Delete, cancellationToken);

    private async ValueTask Delete(GroupName name, ProductName productName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, productName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteProductGroupFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GroupName name, ProductName productName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting group {GroupName} from product {ProductName}...", name, productName);
        await ProductGroupUri.From(name, productName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ProductGroupServices
{
    public static void ConfigureFindProductGroupAction(IServiceCollection services)
    {
        ConfigureTryParseGroupName(services);
        ConfigureProcessProductGroup(services);

        services.TryAddSingleton<FindProductGroupActionHandler>();
        services.TryAddSingleton<FindProductGroupAction>(provider => provider.GetRequiredService<FindProductGroupActionHandler>().Handle);
    }

    private static void ConfigureTryParseGroupName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseGroupNameHandler>();
        services.TryAddSingleton<TryParseGroupName>(provider => provider.GetRequiredService<TryParseGroupNameHandler>().Handle);
    }

    private static void ConfigureProcessProductGroup(IServiceCollection services)
    {
        ConfigureIsGroupNameInSourceControl(services);
        ConfigurePutProductGroup(services);
        ConfigureDeleteProductGroup(services);

        services.TryAddSingleton<ProcessProductGroupHandler>();
        services.TryAddSingleton<ProcessProductGroup>(provider => provider.GetRequiredService<ProcessProductGroupHandler>().Handle);
    }

    private static void ConfigureIsGroupNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsGroupNameInSourceControlHandler>();
        services.TryAddSingleton<IsGroupNameInSourceControl>(provider => provider.GetRequiredService<IsGroupNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutProductGroup(IServiceCollection services)
    {
        ConfigureFindProductGroupDto(services);
        ConfigurePutProductGroupInApim(services);
        ProductServices.ConfigurePutProduct(services);
        GroupServices.ConfigurePutGroup(services);

        services.TryAddSingleton<PutProductGroupHandler>();
        services.TryAddSingleton<PutProductGroup>(provider => provider.GetRequiredService<PutProductGroupHandler>().Handle);
    }

    private static void ConfigureFindProductGroupDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindProductGroupDtoHandler>();
        services.TryAddSingleton<FindProductGroupDto>(provider => provider.GetRequiredService<FindProductGroupDtoHandler>().Handle);
    }

    private static void ConfigurePutProductGroupInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutProductGroupInApimHandler>();
        services.TryAddSingleton<PutProductGroupInApim>(provider => provider.GetRequiredService<PutProductGroupInApimHandler>().Handle);
    }

    private static void ConfigureDeleteProductGroup(IServiceCollection services)
    {
        ConfigureDeleteProductGroupFromApim(services);

        services.TryAddSingleton<DeleteProductGroupHandler>();
        services.TryAddSingleton<DeleteProductGroup>(provider => provider.GetRequiredService<DeleteProductGroupHandler>().Handle);
    }

    private static void ConfigureDeleteProductGroupFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteProductGroupFromApimHandler>();
        services.TryAddSingleton<DeleteProductGroupFromApim>(provider => provider.GetRequiredService<DeleteProductGroupFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ProductGroupPublisher");
}