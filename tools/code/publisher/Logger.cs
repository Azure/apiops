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

internal delegate Option<PublisherAction> FindLoggerAction(FileInfo file);

file delegate Option<LoggerName> TryParseLoggerName(FileInfo file);

file delegate ValueTask ProcessLogger(LoggerName name, CancellationToken cancellationToken);

file delegate bool IsLoggerNameInSourceControl(LoggerName name);

file delegate ValueTask<Option<LoggerDto>> FindLoggerDto(LoggerName name, CancellationToken cancellationToken);

internal delegate ValueTask PutLogger(LoggerName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteLogger(LoggerName name, CancellationToken cancellationToken);

file delegate ValueTask PutLoggerInApim(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteLoggerFromApim(LoggerName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingLogger(LoggerName name, CancellationToken cancellationToken);

file sealed class FindLoggerActionHandler(TryParseLoggerName tryParseName, ProcessLogger processLogger)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(LoggerName name) =>
        async cancellationToken => await processLogger(name, cancellationToken);
}

file sealed class TryParseLoggerNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<LoggerName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<LoggerName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in LoggerInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class LoggerSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<LoggerName> locker = new(LockOptions.Default);
    private ImmutableHashSet<LoggerName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(LoggerName name, Func<LoggerName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessLoggerHandler(IsLoggerNameInSourceControl isNameInSourceControl, PutLogger put, DeleteLogger delete) : IDisposable
{
    private readonly LoggerSemaphore semaphore = new();

    public async ValueTask Handle(LoggerName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(LoggerName name, CancellationToken cancellationToken)
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

file sealed class IsLoggerNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(LoggerName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(LoggerName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = LoggerInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutLoggerHandler(FindLoggerDto findDto, PutLoggerInApim putInApim) : IDisposable
{
    private readonly LoggerSemaphore semaphore = new();

    public async ValueTask Handle(LoggerName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(LoggerName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindLoggerDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<LoggerDto>> Handle(LoggerName name, CancellationToken cancellationToken)
    {
        var informationFile = LoggerInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<LoggerDto>()
               let overrideDto = overrideFactory.Create<LoggerName, LoggerDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutLoggerInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(LoggerName name, LoggerDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting logger {LoggerName}...", name);
        await LoggerUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteLoggerHandler(IEnumerable<OnDeletingLogger> onDeletingHandlers, DeleteLoggerFromApim deleteFromApim) : IDisposable
{
    private readonly LoggerSemaphore semaphore = new();

    public async ValueTask Handle(LoggerName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(LoggerName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteLoggerFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(LoggerName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting logger {LoggerName}...", name);
        await LoggerUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class LoggerServices
{
    public static void ConfigureFindLoggerAction(IServiceCollection services)
    {
        ConfigureTryParseLoggerName(services);
        ConfigureProcessLogger(services);

        services.TryAddSingleton<FindLoggerActionHandler>();
        services.TryAddSingleton<FindLoggerAction>(provider => provider.GetRequiredService<FindLoggerActionHandler>().Handle);
    }

    private static void ConfigureTryParseLoggerName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseLoggerNameHandler>();
        services.TryAddSingleton<TryParseLoggerName>(provider => provider.GetRequiredService<TryParseLoggerNameHandler>().Handle);
    }

    private static void ConfigureProcessLogger(IServiceCollection services)
    {
        ConfigureIsLoggerNameInSourceControl(services);
        ConfigurePutLogger(services);
        ConfigureDeleteLogger(services);

        services.TryAddSingleton<ProcessLoggerHandler>();
        services.TryAddSingleton<ProcessLogger>(provider => provider.GetRequiredService<ProcessLoggerHandler>().Handle);
    }

    private static void ConfigureIsLoggerNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsLoggerNameInSourceControlHandler>();
        services.TryAddSingleton<IsLoggerNameInSourceControl>(provider => provider.GetRequiredService<IsLoggerNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutLogger(IServiceCollection services)
    {
        ConfigureFindLoggerDto(services);
        ConfigurePutLoggerInApim(services);

        services.TryAddSingleton<PutLoggerHandler>();
        services.TryAddSingleton<PutLogger>(provider => provider.GetRequiredService<PutLoggerHandler>().Handle);
    }

    private static void ConfigureFindLoggerDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindLoggerDtoHandler>();
        services.TryAddSingleton<FindLoggerDto>(provider => provider.GetRequiredService<FindLoggerDtoHandler>().Handle);
    }

    private static void ConfigurePutLoggerInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutLoggerInApimHandler>();
        services.TryAddSingleton<PutLoggerInApim>(provider => provider.GetRequiredService<PutLoggerInApimHandler>().Handle);
    }

    private static void ConfigureDeleteLogger(IServiceCollection services)
    {
        ConfigureOnDeletingLogger(services);
        ConfigureDeleteLoggerFromApim(services);

        services.TryAddSingleton<DeleteLoggerHandler>();
        services.TryAddSingleton<DeleteLogger>(provider => provider.GetRequiredService<DeleteLoggerHandler>().Handle);
    }

    private static void ConfigureOnDeletingLogger(IServiceCollection services)
    {
        DiagnosticServices.ConfigureOnDeletingLogger(services);
    }

    private static void ConfigureDeleteLoggerFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteLoggerFromApimHandler>();
        services.TryAddSingleton<DeleteLoggerFromApim>(provider => provider.GetRequiredService<DeleteLoggerFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("LoggerPublisher");
}