using AsyncKeyedLock;
using Azure.Core.Pipeline;
using common;
using DotNext.Threading;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate Option<PublisherAction> FindSubscriptionAction(FileInfo file);

file delegate Option<SubscriptionName> TryParseSubscriptionName(FileInfo file);

file delegate ValueTask ProcessSubscription(SubscriptionName name, CancellationToken cancellationToken);

file delegate bool IsSubscriptionNameInSourceControl(SubscriptionName name);

file delegate ValueTask<Option<SubscriptionDto>> FindSubscriptionDto(SubscriptionName name, CancellationToken cancellationToken);

internal delegate ValueTask PutSubscription(SubscriptionName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteSubscription(SubscriptionName name, CancellationToken cancellationToken);

file delegate ValueTask PutSubscriptionInApim(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteSubscriptionFromApim(SubscriptionName name, CancellationToken cancellationToken);

file delegate FrozenDictionary<SubscriptionName, Func<CancellationToken, ValueTask<Option<SubscriptionDto>>>> GetSubscriptionDtosInPreviousCommit();

file sealed class FindSubscriptionActionHandler(TryParseSubscriptionName tryParseName, ProcessSubscription processSubscription)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(SubscriptionName name) =>
        async cancellationToken => await processSubscription(name, cancellationToken);
}

file sealed class TryParseSubscriptionNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<SubscriptionName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<SubscriptionName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in SubscriptionInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class SubscriptionSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<SubscriptionName> locker = new(LockOptions.Default);
    private ImmutableHashSet<SubscriptionName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(SubscriptionName name, Func<SubscriptionName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync(name, cancellationToken).ConfigureAwait(false);

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

file sealed class ProcessSubscriptionHandler(IsSubscriptionNameInSourceControl isNameInSourceControl, PutSubscription put, DeleteSubscription delete) : IDisposable
{
    private readonly SubscriptionSemaphore semaphore = new();

    public async ValueTask Handle(SubscriptionName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(SubscriptionName name, CancellationToken cancellationToken)
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

file sealed class IsSubscriptionNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(SubscriptionName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(SubscriptionName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = SubscriptionInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutSubscriptionHandler(FindSubscriptionDto findDto,
                                         PutProduct putProduct,
                                         PutApi putApi,
                                         PutSubscriptionInApim putInApim) : IDisposable
{
    private readonly SubscriptionSemaphore semaphore = new();

    public async ValueTask Handle(SubscriptionName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(SubscriptionName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, cancellationToken));
    }

    private async ValueTask Put(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken)
    {
        // Put prerequisites
        await PutProduct(dto, cancellationToken);
        await PutApi(dto, cancellationToken);

        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    private async ValueTask PutProduct(SubscriptionDto dto, CancellationToken cancellationToken) =>
        await SubscriptionModule.TryGetProductName(dto)
                                .IterTask(putProduct.Invoke, cancellationToken);

    private async ValueTask PutApi(SubscriptionDto dto, CancellationToken cancellationToken) =>
        await SubscriptionModule.TryGetApiName(dto)
                                .IterTask(putApi.Invoke, cancellationToken);

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindSubscriptionDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<SubscriptionDto>> Handle(SubscriptionName name, CancellationToken cancellationToken)
    {
        var informationFile = SubscriptionInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<SubscriptionDto>()
               let overrideDto = overrideFactory.Create<SubscriptionName, SubscriptionDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutSubscriptionInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting subscription {SubscriptionName}...", name);
        await SubscriptionUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteSubscriptionHandler(DeleteSubscriptionFromApim deleteFromApim) : IDisposable
{
    private readonly SubscriptionSemaphore semaphore = new();

    public async ValueTask Handle(SubscriptionName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(SubscriptionName name, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteSubscriptionFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(SubscriptionName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting subscription {SubscriptionName}...", name);
        await SubscriptionUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

file sealed class OnDeletingProductHandler(GetSubscriptionDtosInPreviousCommit getDtosInPreviousCommit, ProcessSubscription processSubscription)
{
    private readonly AsyncLazy<FrozenDictionary<ProductName, FrozenSet<SubscriptionName>>> getProductSubscriptions = new(async cancellationToken => await GetProductSubscriptions(getDtosInPreviousCommit, cancellationToken));

    /// <summary>
    /// If a version set is about to be deleted, process the SUBSCRIPTIONs that reference it
    /// </summary>
    public async ValueTask Handle(ProductName name, CancellationToken cancellationToken)
    {
        var subscriptions = await GetProductSubscriptions(name, cancellationToken);

        await subscriptions.IterParallel(processSubscription.Invoke, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ProductName, FrozenSet<SubscriptionName>>> GetProductSubscriptions(GetSubscriptionDtosInPreviousCommit getDtosInPreviousCommit, CancellationToken cancellationToken) =>
        await getDtosInPreviousCommit()
                .ToAsyncEnumerable()
                .Choose(async kvp =>
                {
                    var dtoOption = await kvp.Value(cancellationToken);

                    return from dto in dtoOption
                           from productName in SubscriptionModule.TryGetProductName(dto)
                           select (ProductName: productName, SubscriptionName: kvp.Key);
                })
                .GroupBy(x => x.ProductName, x => x.SubscriptionName)
                .SelectAwait(async group => (group.Key, await group.ToFrozenSet(cancellationToken)))
                .ToFrozenDictionary(cancellationToken);

    private async ValueTask<FrozenSet<SubscriptionName>> GetProductSubscriptions(ProductName name, CancellationToken cancellationToken)
    {
        var productSubscriptions = await getProductSubscriptions.WithCancellation(cancellationToken);

#pragma warning disable CA1849 // Call async methods when in an async method
        return productSubscriptions.Find(name)
                             .IfNone(FrozenSet<SubscriptionName>.Empty);
#pragma warning restore CA1849 // Call async methods when in an async method
    }
}

file sealed class GetSubscriptionDtosInPreviousCommitHandler(GetArtifactsInPreviousCommit getArtifactsInPreviousCommit, ManagementServiceDirectory serviceDirectory)
{
    private readonly Lazy<FrozenDictionary<SubscriptionName, Func<CancellationToken, ValueTask<Option<SubscriptionDto>>>>> dtosInPreviousCommit = new(() => GetDtos(getArtifactsInPreviousCommit, serviceDirectory));

    public FrozenDictionary<SubscriptionName, Func<CancellationToken, ValueTask<Option<SubscriptionDto>>>> Handle() =>
        dtosInPreviousCommit.Value;

    private static FrozenDictionary<SubscriptionName, Func<CancellationToken, ValueTask<Option<SubscriptionDto>>>> GetDtos(GetArtifactsInPreviousCommit getArtifactsInPreviousCommit, ManagementServiceDirectory serviceDirectory) =>
        getArtifactsInPreviousCommit()
            .Choose(kvp => from subscriptionName in TryGetNameFromInformationFile(kvp.Key, serviceDirectory)
                           select (subscriptionName, TryGetDto(kvp.Value)))
            .ToFrozenDictionary();

    private static Option<SubscriptionName> TryGetNameFromInformationFile(FileInfo file, ManagementServiceDirectory serviceDirectory) =>
        from informationFile in SubscriptionInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;

    private static Func<CancellationToken, ValueTask<Option<SubscriptionDto>>> TryGetDto(Func<CancellationToken, ValueTask<Option<BinaryData>>> tryGetContents) =>
        async cancellationToken =>
        {
            var contentsOption = await tryGetContents(cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<SubscriptionDto>();
        };
}

file sealed class OnDeletingApiHandler(GetSubscriptionDtosInPreviousCommit getDtosInPreviousCommit, ProcessSubscription processSubscription)
{
    private readonly AsyncLazy<FrozenDictionary<ApiName, FrozenSet<SubscriptionName>>> getApiSubscriptions = new(async cancellationToken => await GetApiSubscriptions(getDtosInPreviousCommit, cancellationToken));

    /// <summary>
    /// If a version set is about to be deleted, process the SUBSCRIPTIONs that reference it
    /// </summary>
    public async ValueTask Handle(ApiName name, CancellationToken cancellationToken)
    {
        var subscriptions = await GetApiSubscriptions(name, cancellationToken);

        await subscriptions.IterParallel(processSubscription.Invoke, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<ApiName, FrozenSet<SubscriptionName>>> GetApiSubscriptions(GetSubscriptionDtosInPreviousCommit getDtosInPreviousCommit, CancellationToken cancellationToken) =>
        await getDtosInPreviousCommit()
                .ToAsyncEnumerable()
                .Choose(async kvp =>
                {
                    var dtoOption = await kvp.Value(cancellationToken);

                    return from dto in dtoOption
                           from apiName in SubscriptionModule.TryGetApiName(dto)
                           select (ApiName: apiName, SubscriptionName: kvp.Key);
                })
                .GroupBy(x => x.ApiName, x => x.SubscriptionName)
                .SelectAwait(async group => (group.Key, await group.ToFrozenSet(cancellationToken)))
                .ToFrozenDictionary(cancellationToken);

    private async ValueTask<FrozenSet<SubscriptionName>> GetApiSubscriptions(ApiName name, CancellationToken cancellationToken)
    {
        var apiSubscriptions = await getApiSubscriptions.WithCancellation(cancellationToken);

#pragma warning disable CA1849 // Call async methods when in an async method
        return apiSubscriptions.Find(name)
                             .IfNone(FrozenSet<SubscriptionName>.Empty);
#pragma warning restore CA1849 // Call async methods when in an async method
    }
}

internal static class SubscriptionServices
{
    public static void ConfigureFindSubscriptionAction(IServiceCollection services)
    {
        ConfigureTryParseSubscriptionName(services);
        ConfigureProcessSubscription(services);

        services.TryAddSingleton<FindSubscriptionActionHandler>();
        services.TryAddSingleton<FindSubscriptionAction>(provider => provider.GetRequiredService<FindSubscriptionActionHandler>().Handle);
    }

    private static void ConfigureTryParseSubscriptionName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseSubscriptionNameHandler>();
        services.TryAddSingleton<TryParseSubscriptionName>(provider => provider.GetRequiredService<TryParseSubscriptionNameHandler>().Handle);
    }

    private static void ConfigureProcessSubscription(IServiceCollection services)
    {
        ConfigureIsSubscriptionNameInSourceControl(services);
        ConfigurePutSubscription(services);
        ConfigureDeleteSubscription(services);

        services.TryAddSingleton<ProcessSubscriptionHandler>();
        services.TryAddSingleton<ProcessSubscription>(provider => provider.GetRequiredService<ProcessSubscriptionHandler>().Handle);
    }

    private static void ConfigureIsSubscriptionNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsSubscriptionNameInSourceControlHandler>();
        services.TryAddSingleton<IsSubscriptionNameInSourceControl>(provider => provider.GetRequiredService<IsSubscriptionNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutSubscription(IServiceCollection services)
    {
        ConfigureFindSubscriptionDto(services);
        ConfigurePutSubscriptionInApim(services);
        ProductServices.ConfigurePutProduct(services);
        ApiServices.ConfigurePutApi(services);

        services.TryAddSingleton<PutSubscriptionHandler>();
        services.TryAddSingleton<PutSubscription>(provider => provider.GetRequiredService<PutSubscriptionHandler>().Handle);
    }

    private static void ConfigureFindSubscriptionDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindSubscriptionDtoHandler>();
        services.TryAddSingleton<FindSubscriptionDto>(provider => provider.GetRequiredService<FindSubscriptionDtoHandler>().Handle);
    }

    private static void ConfigurePutSubscriptionInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutSubscriptionInApimHandler>();
        services.TryAddSingleton<PutSubscriptionInApim>(provider => provider.GetRequiredService<PutSubscriptionInApimHandler>().Handle);
    }

    private static void ConfigureDeleteSubscription(IServiceCollection services)
    {
        ConfigureDeleteSubscriptionFromApim(services);

        services.TryAddSingleton<DeleteSubscriptionHandler>();
        services.TryAddSingleton<DeleteSubscription>(provider => provider.GetRequiredService<DeleteSubscriptionHandler>().Handle);
    }

    private static void ConfigureDeleteSubscriptionFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteSubscriptionFromApimHandler>();
        services.TryAddSingleton<DeleteSubscriptionFromApim>(provider => provider.GetRequiredService<DeleteSubscriptionFromApimHandler>().Handle);
    }

    public static void ConfigureOnDeletingProduct(IServiceCollection services)
    {
        ConfigureGetSubscriptionDtosInPreviousCommit(services);

        services.TryAddSingleton<OnDeletingProductHandler>();

        // We use AddSingleton instead of TryAddSingleton to support multiple registrations
        services.AddSingleton<OnDeletingProduct>(provider => provider.GetRequiredService<OnDeletingProductHandler>().Handle);
    }

    private static void ConfigureGetSubscriptionDtosInPreviousCommit(IServiceCollection services)
    {
        services.TryAddSingleton<GetSubscriptionDtosInPreviousCommitHandler>();
        services.TryAddSingleton<GetSubscriptionDtosInPreviousCommit>(provider => provider.GetRequiredService<GetSubscriptionDtosInPreviousCommitHandler>().Handle);
    }

    public static void ConfigureOnDeletingApi(IServiceCollection services)
    {
        ConfigureGetSubscriptionDtosInPreviousCommit(services);

        services.TryAddSingleton<OnDeletingApiHandler>();

        // We use AddSingleton instead of TryAddSingleton to support multiple registrations
        services.AddSingleton<OnDeletingApi>(provider => provider.GetRequiredService<OnDeletingApiHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("SubscriptionPublisher");
}