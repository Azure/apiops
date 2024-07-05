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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask ProcessBackendsToPut(CancellationToken cancellationToken);
internal delegate ValueTask ProcessDeletedBackends(CancellationToken cancellationToken);

file delegate Option<BackendName> TryParseBackendName(FileInfo file);

file delegate ValueTask ProcessBackend(BackendName name, CancellationToken cancellationToken);

file delegate bool IsBackendNameInSourceControl(BackendName name);

file delegate ValueTask<Option<BackendDto>> FindBackendDto(BackendName name, CancellationToken cancellationToken);

internal delegate ValueTask PutBackend(BackendName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteBackend(BackendName name, CancellationToken cancellationToken);

file delegate ValueTask PutBackendInApim(BackendName name, BackendDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteBackendFromApim(BackendName name, CancellationToken cancellationToken);

file sealed class ProcessBackendsToPutHandler(GetPublisherFiles getPublisherFiles,
                                              TryParseBackendName tryParseBackendName,
                                              IsBackendNameInSourceControl isNameInSourceControl,
                                              PutBackend putBackend)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await getPublisherFiles()
                .Choose(tryParseBackendName.Invoke)
                .Where(isNameInSourceControl.Invoke)
                .IterParallel(putBackend.Invoke, cancellationToken);
}

file sealed class ProcessDeletedBackendsHandler(GetPublisherFiles getPublisherFiles,
                                                TryParseBackendName tryParseBackendName,
                                                IsBackendNameInSourceControl isNameInSourceControl,
                                                DeleteBackend deleteBackend)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await getPublisherFiles()
                .Choose(tryParseBackendName.Invoke)
                .Where(name => isNameInSourceControl(name) is false)
                .IterParallel(deleteBackend.Invoke, cancellationToken);
}

file sealed class FindBackendActionHandler(TryParseBackendName tryParseName, ProcessBackend processBackend)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(BackendName name) =>
        async cancellationToken => await processBackend(name, cancellationToken);
}

file sealed class TryParseBackendNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<BackendName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<BackendName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in BackendInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class BackendSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<BackendName> locker = new(LockOptions.Default);
    private ImmutableHashSet<BackendName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(BackendName name, Func<BackendName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessBackendHandler(IsBackendNameInSourceControl isNameInSourceControl, PutBackend put, DeleteBackend delete) : IDisposable
{
    private readonly BackendSemaphore semaphore = new();

    public async ValueTask Handle(BackendName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(BackendName name, CancellationToken cancellationToken)
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

file sealed class IsBackendNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(BackendName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(BackendName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = BackendInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutBackendHandler(FindBackendDto findDto, PutBackendInApim putInApim) : IDisposable
{
    private readonly BackendSemaphore semaphore = new();

    public async ValueTask Handle(BackendName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(BackendName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindBackendDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<BackendDto>> Handle(BackendName name, CancellationToken cancellationToken)
    {
        var informationFile = BackendInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<BackendDto>()
               let overrideDto = overrideFactory.Create<BackendName, BackendDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutBackendInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(BackendName name, BackendDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting backend {BackendName}...", name);
        await BackendUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteBackendHandler(DeleteBackendFromApim deleteFromApim) : IDisposable
{
    private readonly BackendSemaphore semaphore = new();

    public async ValueTask Handle(BackendName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(BackendName name, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteBackendFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(BackendName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting backend {BackendName}...", name);
        await BackendUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class BackendServices
{
    public static void ConfigureProcessBackendsToPut(IServiceCollection services)
    {
        ConfigureTryParseBackendName(services);
        ConfigureIsBackendNameInSourceControl(services);
        ConfigurePutBackend(services);

        services.TryAddSingleton<ProcessBackendsToPutHandler>();
        services.TryAddSingleton<ProcessBackendsToPut>(provider => provider.GetRequiredService<ProcessBackendsToPutHandler>().Handle);
    }

    private static void ConfigureTryParseBackendName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseBackendNameHandler>();
        services.TryAddSingleton<TryParseBackendName>(provider => provider.GetRequiredService<TryParseBackendNameHandler>().Handle);
    }

    private static void ConfigureProcessBackend(IServiceCollection services)
    {
        ConfigureIsBackendNameInSourceControl(services);
        ConfigurePutBackend(services);
        ConfigureDeleteBackend(services);

        services.TryAddSingleton<ProcessBackendHandler>();
        services.TryAddSingleton<ProcessBackend>(provider => provider.GetRequiredService<ProcessBackendHandler>().Handle);
    }

    private static void ConfigureIsBackendNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsBackendNameInSourceControlHandler>();
        services.TryAddSingleton<IsBackendNameInSourceControl>(provider => provider.GetRequiredService<IsBackendNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutBackend(IServiceCollection services)
    {
        ConfigureFindBackendDto(services);
        ConfigurePutBackendInApim(services);

        services.TryAddSingleton<PutBackendHandler>();
        services.TryAddSingleton<PutBackend>(provider => provider.GetRequiredService<PutBackendHandler>().Handle);
    }

    private static void ConfigureFindBackendDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindBackendDtoHandler>();
        services.TryAddSingleton<FindBackendDto>(provider => provider.GetRequiredService<FindBackendDtoHandler>().Handle);
    }

    private static void ConfigurePutBackendInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutBackendInApimHandler>();
        services.TryAddSingleton<PutBackendInApim>(provider => provider.GetRequiredService<PutBackendInApimHandler>().Handle);
    }

    public static void ConfigureProcessDeletedBackends(IServiceCollection services)
    {
        ConfigureTryParseBackendName(services);
        ConfigureIsBackendNameInSourceControl(services);
        ConfigureDeleteBackend(services);

        services.TryAddSingleton<ProcessDeletedBackendsHandler>();
        services.TryAddSingleton<ProcessDeletedBackends>(provider => provider.GetRequiredService<ProcessDeletedBackendsHandler>().Handle);
    }

    private static void ConfigureDeleteBackend(IServiceCollection services)
    {
        ConfigureDeleteBackendFromApim(services);

        services.TryAddSingleton<DeleteBackendHandler>();
        services.TryAddSingleton<DeleteBackend>(provider => provider.GetRequiredService<DeleteBackendHandler>().Handle);
    }

    private static void ConfigureDeleteBackendFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteBackendFromApimHandler>();
        services.TryAddSingleton<DeleteBackendFromApim>(provider => provider.GetRequiredService<DeleteBackendFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("BackendPublisher");
}