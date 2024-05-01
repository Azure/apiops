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

internal delegate ValueTask ProcessNamedValuesToPut(CancellationToken cancellationToken);

internal delegate ValueTask ProcessDeletedNamedValues(CancellationToken cancellationToken);

file delegate Option<NamedValueName> TryParseNamedValueName(FileInfo file);

file delegate bool IsNamedValueNameInSourceControl(NamedValueName name);

file delegate ValueTask<Option<NamedValueDto>> FindNamedValueDto(NamedValueName name, CancellationToken cancellationToken);

internal delegate ValueTask PutNamedValue(NamedValueName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteNamedValue(NamedValueName name, CancellationToken cancellationToken);

file delegate ValueTask PutNamedValueInApim(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteNamedValueFromApim(NamedValueName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingNamedValue(NamedValueName name, CancellationToken cancellationToken);

file sealed class ProcessNamedValuesToPutHandler(GetPublisherFiles getPublisherFiles,
                                                 TryParseNamedValueName tryParseNamedValueName,
                                                 IsNamedValueNameInSourceControl isNameInSourceControl,
                                                 PutNamedValue putNamedValue)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await getPublisherFiles()
                .Choose(tryParseNamedValueName.Invoke)
                .Where(isNameInSourceControl.Invoke)
                .IterParallel(putNamedValue.Invoke, cancellationToken);
}

file sealed class ProcessDeletedNamedValuesHandler(GetPublisherFiles getPublisherFiles,
                                                   TryParseNamedValueName tryParseNamedValueName,
                                                   IsNamedValueNameInSourceControl isNameInSourceControl,
                                                   DeleteNamedValue deleteNamedValue)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await getPublisherFiles()
                .Choose(tryParseNamedValueName.Invoke)
                .Where(name => isNameInSourceControl(name) is false)
                .IterParallel(deleteNamedValue.Invoke, cancellationToken);
}

file sealed class TryParseNamedValueNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<NamedValueName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<NamedValueName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in NamedValueInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

file sealed class IsNamedValueNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(NamedValueName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(NamedValueName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = NamedValueInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutNamedValueHandler(FindNamedValueDto findDto, PutNamedValueInApim putInApim) : IDisposable
{
    private readonly NamedValueSemaphore semaphore = new();

    public async ValueTask Handle(NamedValueName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(NamedValueName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class NamedValueSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<NamedValueName> locker = new(LockOptions.Default);
    private ImmutableHashSet<NamedValueName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(NamedValueName name, Func<NamedValueName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class FindNamedValueDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<NamedValueDto>> Handle(NamedValueName name, CancellationToken cancellationToken)
    {
        var informationFile = NamedValueInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<NamedValueDto>()
               let overrideDto = overrideFactory.Create<NamedValueName, NamedValueDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutNamedValueInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken)
    {
        if (dto.Properties.Secret is true && dto.Properties.Value is null && dto.Properties.KeyVault?.SecretIdentifier is null)
        {
            logger.LogWarning("Named value {NamedValueName} is secret, but no value or keyvault identifier was specified. Skipping it...", name);
            return;
        }

        logger.LogInformation("Putting named value {NamedValueName}...", name);
        await NamedValueUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteNamedValueHandler(DeleteNamedValueFromApim deleteFromApim) : IDisposable
{
    private readonly NamedValueSemaphore semaphore = new();

    public async ValueTask Handle(NamedValueName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(NamedValueName name, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteNamedValueFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(NamedValueName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting named value {NamedValueName}...", name);
        await NamedValueUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class NamedValueServices
{
    public static void ConfigureProcessNamedValuesToPut(IServiceCollection services)
    {
        ConfigureTryParseNamedValueName(services);
        ConfigureIsNamedValueNameInSourceControl(services);
        ConfigurePutNamedValue(services);

        services.TryAddSingleton<ProcessNamedValuesToPutHandler>();
        services.TryAddSingleton<ProcessNamedValuesToPut>(provider => provider.GetRequiredService<ProcessNamedValuesToPutHandler>().Handle);
    }

    private static void ConfigureTryParseNamedValueName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseNamedValueNameHandler>();
        services.TryAddSingleton<TryParseNamedValueName>(provider => provider.GetRequiredService<TryParseNamedValueNameHandler>().Handle);
    }

    private static void ConfigureIsNamedValueNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsNamedValueNameInSourceControlHandler>();
        services.TryAddSingleton<IsNamedValueNameInSourceControl>(provider => provider.GetRequiredService<IsNamedValueNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutNamedValue(IServiceCollection services)
    {
        ConfigureFindNamedValueDto(services);
        ConfigurePutNamedValueInApim(services);

        services.TryAddSingleton<PutNamedValueHandler>();
        services.TryAddSingleton<PutNamedValue>(provider => provider.GetRequiredService<PutNamedValueHandler>().Handle);
    }

    private static void ConfigureFindNamedValueDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindNamedValueDtoHandler>();
        services.TryAddSingleton<FindNamedValueDto>(provider => provider.GetRequiredService<FindNamedValueDtoHandler>().Handle);
    }

    private static void ConfigurePutNamedValueInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutNamedValueInApimHandler>();
        services.TryAddSingleton<PutNamedValueInApim>(provider => provider.GetRequiredService<PutNamedValueInApimHandler>().Handle);
    }

    public static void ConfigureProcessDeletedNamedValues(IServiceCollection services)
    {
        ConfigureTryParseNamedValueName(services);
        ConfigureIsNamedValueNameInSourceControl(services);
        ConfigureDeleteNamedValue(services);

        services.TryAddSingleton<ProcessDeletedNamedValuesHandler>();
        services.TryAddSingleton<ProcessDeletedNamedValues>(provider => provider.GetRequiredService<ProcessDeletedNamedValuesHandler>().Handle);
    }

    private static void ConfigureDeleteNamedValue(IServiceCollection services)
    {
        ConfigureDeleteNamedValueFromApim(services);

        services.TryAddSingleton<DeleteNamedValueHandler>();
        services.TryAddSingleton<DeleteNamedValue>(provider => provider.GetRequiredService<DeleteNamedValueHandler>().Handle);
    }

    private static void ConfigureDeleteNamedValueFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteNamedValueFromApimHandler>();
        services.TryAddSingleton<DeleteNamedValueFromApim>(provider => provider.GetRequiredService<DeleteNamedValueFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("NamedValuePublisher");
}