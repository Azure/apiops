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

internal delegate Option<PublisherAction> FindDiagnosticAction(FileInfo file);

file delegate Option<DiagnosticName> TryParseDiagnosticName(FileInfo file);

file delegate ValueTask ProcessDiagnostic(DiagnosticName name, CancellationToken cancellationToken);

file delegate bool IsDiagnosticNameInSourceControl(DiagnosticName name);

file delegate ValueTask<Option<DiagnosticDto>> FindDiagnosticDto(DiagnosticName name, CancellationToken cancellationToken);

internal delegate ValueTask PutDiagnostic(DiagnosticName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteDiagnostic(DiagnosticName name, CancellationToken cancellationToken);

file delegate ValueTask PutDiagnosticInApim(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteDiagnosticFromApim(DiagnosticName name, CancellationToken cancellationToken);

file delegate FrozenDictionary<DiagnosticName, Func<CancellationToken, ValueTask<Option<DiagnosticDto>>>> GetDiagnosticDtosInPreviousCommit();

file sealed class FindDiagnosticActionHandler(TryParseDiagnosticName tryParseName, ProcessDiagnostic processDiagnostic)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(DiagnosticName name) =>
        async cancellationToken => await processDiagnostic(name, cancellationToken);
}

file sealed class TryParseDiagnosticNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<DiagnosticName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<DiagnosticName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in DiagnosticInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class DiagnosticSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<DiagnosticName> locker = new(LockOptions.Default);
    private ImmutableHashSet<DiagnosticName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(DiagnosticName name, Func<DiagnosticName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessDiagnosticHandler(IsDiagnosticNameInSourceControl isNameInSourceControl, PutDiagnostic put, DeleteDiagnostic delete) : IDisposable
{
    private readonly DiagnosticSemaphore semaphore = new();

    public async ValueTask Handle(DiagnosticName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(DiagnosticName name, CancellationToken cancellationToken)
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

file sealed class IsDiagnosticNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(DiagnosticName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(DiagnosticName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutDiagnosticHandler(FindDiagnosticDto findDto, PutLogger putLogger, PutDiagnosticInApim putInApim) : IDisposable
{
    private readonly DiagnosticSemaphore semaphore = new();

    public async ValueTask Handle(DiagnosticName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(DiagnosticName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, cancellationToken));
    }

    private async ValueTask Put(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken)
    {
        // Put prerequisites
        await PutLogger(dto, cancellationToken);

        await putInApim(name, dto, cancellationToken);
    }

    private async ValueTask PutLogger(DiagnosticDto dto, CancellationToken cancellationToken) =>
        await DiagnosticModule.TryGetLoggerName(dto)
                              .IterTask(putLogger.Invoke, cancellationToken);

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindDiagnosticDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<DiagnosticDto>> Handle(DiagnosticName name, CancellationToken cancellationToken)
    {
        var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<DiagnosticDto>()
               let overrideDto = overrideFactory.Create<DiagnosticName, DiagnosticDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutDiagnosticInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting diagnostic {DiagnosticName}...", name);
        await DiagnosticUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteDiagnosticHandler(DeleteDiagnosticFromApim deleteFromApim) : IDisposable
{
    private readonly DiagnosticSemaphore semaphore = new();

    public async ValueTask Handle(DiagnosticName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(DiagnosticName name, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteDiagnosticFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(DiagnosticName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting diagnostic {DiagnosticName}...", name);
        await DiagnosticUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

file sealed class OnDeletingLoggerHandler(GetDiagnosticDtosInPreviousCommit getDtosInPreviousCommit, ProcessDiagnostic processDiagnostic)
{
    private readonly AsyncLazy<FrozenDictionary<LoggerName, FrozenSet<DiagnosticName>>> getLoggerDiagnostics = new(async cancellationToken => await GetLoggerDiagnostics(getDtosInPreviousCommit, cancellationToken));

    /// <summary>
    /// If a version set is about to be deleted, process the diagnostics that reference it
    /// </summary>
    public async ValueTask Handle(LoggerName name, CancellationToken cancellationToken)
    {
        var diagnostics = await GetLoggerDiagnostics(name, cancellationToken);

        await diagnostics.IterParallel(processDiagnostic.Invoke, cancellationToken);
    }

    private static async ValueTask<FrozenDictionary<LoggerName, FrozenSet<DiagnosticName>>> GetLoggerDiagnostics(GetDiagnosticDtosInPreviousCommit getDtosInPreviousCommit, CancellationToken cancellationToken) =>
        await getDtosInPreviousCommit()
                .ToAsyncEnumerable()
                .Choose(async kvp =>
                {
                    var dtoOption = await kvp.Value(cancellationToken);

                    return from dto in dtoOption
                           from loggerName in DiagnosticModule.TryGetLoggerName(dto)
                           select (LoggerName: loggerName, DiagnosticName: kvp.Key);
                })
                .GroupBy(x => x.LoggerName, x => x.DiagnosticName)
                .SelectAwait(async group => (group.Key, await group.ToFrozenSet(cancellationToken)))
                .ToFrozenDictionary(cancellationToken);

    private async ValueTask<FrozenSet<DiagnosticName>> GetLoggerDiagnostics(LoggerName name, CancellationToken cancellationToken)
    {
        var loggerDiagnostics = await getLoggerDiagnostics.WithCancellation(cancellationToken);

#pragma warning disable CA1849 // Call async methods when in an async method
        return loggerDiagnostics.Find(name)
                             .IfNone(FrozenSet<DiagnosticName>.Empty);
#pragma warning restore CA1849 // Call async methods when in an async method
    }
}

file sealed class GetDiagnosticDtosInPreviousCommitHandler(GetArtifactsInPreviousCommit getArtifactsInPreviousCommit, ManagementServiceDirectory serviceDirectory)
{
    private readonly Lazy<FrozenDictionary<DiagnosticName, Func<CancellationToken, ValueTask<Option<DiagnosticDto>>>>> dtosInPreviousCommit = new(() => GetDtos(getArtifactsInPreviousCommit, serviceDirectory));

    public FrozenDictionary<DiagnosticName, Func<CancellationToken, ValueTask<Option<DiagnosticDto>>>> Handle() =>
        dtosInPreviousCommit.Value;

    private static FrozenDictionary<DiagnosticName, Func<CancellationToken, ValueTask<Option<DiagnosticDto>>>> GetDtos(GetArtifactsInPreviousCommit getArtifactsInPreviousCommit, ManagementServiceDirectory serviceDirectory) =>
        getArtifactsInPreviousCommit()
            .Choose(kvp => from diagnosticName in TryGetNameFromInformationFile(kvp.Key, serviceDirectory)
                           select (diagnosticName, TryGetDto(kvp.Value)))
            .ToFrozenDictionary();

    private static Option<DiagnosticName> TryGetNameFromInformationFile(FileInfo file, ManagementServiceDirectory serviceDirectory) =>
        from informationFile in DiagnosticInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;

    private static Func<CancellationToken, ValueTask<Option<DiagnosticDto>>> TryGetDto(Func<CancellationToken, ValueTask<Option<BinaryData>>> tryGetContents) =>
        async cancellationToken =>
        {
            var contentsOption = await tryGetContents(cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<DiagnosticDto>();
        };
}

internal static class DiagnosticServices
{
    public static void ConfigureFindDiagnosticAction(IServiceCollection services)
    {
        ConfigureTryParseDiagnosticName(services);
        ConfigureProcessDiagnostic(services);

        services.TryAddSingleton<FindDiagnosticActionHandler>();
        services.TryAddSingleton<FindDiagnosticAction>(provider => provider.GetRequiredService<FindDiagnosticActionHandler>().Handle);
    }

    private static void ConfigureTryParseDiagnosticName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseDiagnosticNameHandler>();
        services.TryAddSingleton<TryParseDiagnosticName>(provider => provider.GetRequiredService<TryParseDiagnosticNameHandler>().Handle);
    }

    private static void ConfigureProcessDiagnostic(IServiceCollection services)
    {
        ConfigureIsDiagnosticNameInSourceControl(services);
        ConfigurePutDiagnostic(services);
        ConfigureDeleteDiagnostic(services);

        services.TryAddSingleton<ProcessDiagnosticHandler>();
        services.TryAddSingleton<ProcessDiagnostic>(provider => provider.GetRequiredService<ProcessDiagnosticHandler>().Handle);
    }

    private static void ConfigureIsDiagnosticNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsDiagnosticNameInSourceControlHandler>();
        services.TryAddSingleton<IsDiagnosticNameInSourceControl>(provider => provider.GetRequiredService<IsDiagnosticNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutDiagnostic(IServiceCollection services)
    {
        ConfigureFindDiagnosticDto(services);
        ConfigurePutDiagnosticInApim(services);
        LoggerServices.ConfigurePutLogger(services);

        services.TryAddSingleton<PutDiagnosticHandler>();
        services.TryAddSingleton<PutDiagnostic>(provider => provider.GetRequiredService<PutDiagnosticHandler>().Handle);
    }

    private static void ConfigureFindDiagnosticDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindDiagnosticDtoHandler>();
        services.TryAddSingleton<FindDiagnosticDto>(provider => provider.GetRequiredService<FindDiagnosticDtoHandler>().Handle);
    }

    private static void ConfigurePutDiagnosticInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutDiagnosticInApimHandler>();
        services.TryAddSingleton<PutDiagnosticInApim>(provider => provider.GetRequiredService<PutDiagnosticInApimHandler>().Handle);
    }

    private static void ConfigureDeleteDiagnostic(IServiceCollection services)
    {
        ConfigureDeleteDiagnosticFromApim(services);

        services.TryAddSingleton<DeleteDiagnosticHandler>();
        services.TryAddSingleton<DeleteDiagnostic>(provider => provider.GetRequiredService<DeleteDiagnosticHandler>().Handle);
    }

    private static void ConfigureDeleteDiagnosticFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteDiagnosticFromApimHandler>();
        services.TryAddSingleton<DeleteDiagnosticFromApim>(provider => provider.GetRequiredService<DeleteDiagnosticFromApimHandler>().Handle);
    }

    public static void ConfigureOnDeletingLogger(IServiceCollection services)
    {
        ConfigureGetDiagnosticDtosInPreviousCommit(services);

        services.TryAddSingleton<OnDeletingLoggerHandler>();

        // We use AddSingleton instead of TryAddSingleton to support multiple registrations
        services.AddSingleton<OnDeletingLogger>(provider => provider.GetRequiredService<OnDeletingLoggerHandler>().Handle);
    }

    private static void ConfigureGetDiagnosticDtosInPreviousCommit(IServiceCollection services)
    {
        services.TryAddSingleton<GetDiagnosticDtosInPreviousCommitHandler>();
        services.TryAddSingleton<GetDiagnosticDtosInPreviousCommit>(provider => provider.GetRequiredService<GetDiagnosticDtosInPreviousCommitHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("DiagnosticPublisher");
}