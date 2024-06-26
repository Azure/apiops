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
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate Option<PublisherAction> FindVersionSetAction(FileInfo file);

file delegate Option<VersionSetName> TryParseVersionSetName(FileInfo file);

file delegate ValueTask ProcessVersionSet(VersionSetName name, CancellationToken cancellationToken);

file delegate bool IsVersionSetNameInSourceControl(VersionSetName name);

file delegate ValueTask<Option<VersionSetDto>> FindVersionSetDto(VersionSetName name, CancellationToken cancellationToken);

internal delegate ValueTask PutVersionSet(VersionSetName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteVersionSet(VersionSetName name, CancellationToken cancellationToken);

file delegate ValueTask PutVersionSetInApim(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteVersionSetFromApim(VersionSetName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingVersionSet(VersionSetName name, CancellationToken cancellationToken);

file sealed class FindVersionSetActionHandler(TryParseVersionSetName tryParseName, ProcessVersionSet processVersionSet)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(VersionSetName name) =>
        async cancellationToken => await processVersionSet(name, cancellationToken);
}

file sealed class TryParseVersionSetNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<VersionSetName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<VersionSetName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in VersionSetInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class VersionSetSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<VersionSetName> locker = new(LockOptions.Default);
    private ImmutableHashSet<VersionSetName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(VersionSetName name, Func<VersionSetName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessVersionSetHandler(IsVersionSetNameInSourceControl isNameInSourceControl, PutVersionSet put, DeleteVersionSet delete) : IDisposable
{
    private readonly VersionSetSemaphore semaphore = new();

    public async ValueTask Handle(VersionSetName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(VersionSetName name, CancellationToken cancellationToken)
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

file sealed class IsVersionSetNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(VersionSetName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(VersionSetName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = VersionSetInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutVersionSetHandler(FindVersionSetDto findDto, PutVersionSetInApim putInApim) : IDisposable
{
    private readonly VersionSetSemaphore semaphore = new();

    public async ValueTask Handle(VersionSetName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(VersionSetName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindVersionSetDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<VersionSetDto>> Handle(VersionSetName name, CancellationToken cancellationToken)
    {
        var informationFile = VersionSetInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<VersionSetDto>()
               let overrideDto = overrideFactory.Create<VersionSetName, VersionSetDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutVersionSetInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting version set {VersionSetName}...", name);
        await VersionSetUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteVersionSetHandler(IEnumerable<OnDeletingVersionSet> onDeletingHandlers, DeleteVersionSetFromApim deleteFromApim) : IDisposable
{
    private readonly VersionSetSemaphore semaphore = new();

    public async ValueTask Handle(VersionSetName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(VersionSetName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteVersionSetFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(VersionSetName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting version set {VersionSetName}...", name);
        await VersionSetUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class VersionSetServices
{
    public static void ConfigureFindVersionSetAction(IServiceCollection services)
    {
        ConfigureTryParseVersionSetName(services);
        ConfigureProcessVersionSet(services);

        services.TryAddSingleton<FindVersionSetActionHandler>();
        services.TryAddSingleton<FindVersionSetAction>(provider => provider.GetRequiredService<FindVersionSetActionHandler>().Handle);
    }

    private static void ConfigureTryParseVersionSetName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseVersionSetNameHandler>();
        services.TryAddSingleton<TryParseVersionSetName>(provider => provider.GetRequiredService<TryParseVersionSetNameHandler>().Handle);
    }

    private static void ConfigureProcessVersionSet(IServiceCollection services)
    {
        ConfigureIsVersionSetNameInSourceControl(services);
        ConfigurePutVersionSet(services);
        ConfigureDeleteVersionSet(services);

        services.TryAddSingleton<ProcessVersionSetHandler>();
        services.TryAddSingleton<ProcessVersionSet>(provider => provider.GetRequiredService<ProcessVersionSetHandler>().Handle);
    }

    private static void ConfigureIsVersionSetNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsVersionSetNameInSourceControlHandler>();
        services.TryAddSingleton<IsVersionSetNameInSourceControl>(provider => provider.GetRequiredService<IsVersionSetNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutVersionSet(IServiceCollection services)
    {
        ConfigureFindVersionSetDto(services);
        ConfigurePutVersionSetInApim(services);

        services.TryAddSingleton<PutVersionSetHandler>();
        services.TryAddSingleton<PutVersionSet>(provider => provider.GetRequiredService<PutVersionSetHandler>().Handle);
    }

    private static void ConfigureFindVersionSetDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindVersionSetDtoHandler>();
        services.TryAddSingleton<FindVersionSetDto>(provider => provider.GetRequiredService<FindVersionSetDtoHandler>().Handle);
    }

    private static void ConfigurePutVersionSetInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutVersionSetInApimHandler>();
        services.TryAddSingleton<PutVersionSetInApim>(provider => provider.GetRequiredService<PutVersionSetInApimHandler>().Handle);
    }

    private static void ConfigureDeleteVersionSet(IServiceCollection services)
    {
        ConfigureOnDeletingVersionSet(services);
        ConfigureDeleteVersionSetFromApim(services);

        services.TryAddSingleton<DeleteVersionSetHandler>();
        services.TryAddSingleton<DeleteVersionSet>(provider => provider.GetRequiredService<DeleteVersionSetHandler>().Handle);
    }

    private static void ConfigureOnDeletingVersionSet(IServiceCollection services)
    {
        ApiServices.ConfigureOnDeletingVersionSet(services);
    }

    private static void ConfigureDeleteVersionSetFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteVersionSetFromApimHandler>();
        services.TryAddSingleton<DeleteVersionSetFromApim>(provider => provider.GetRequiredService<DeleteVersionSetFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("VersionSetPublisher");
}