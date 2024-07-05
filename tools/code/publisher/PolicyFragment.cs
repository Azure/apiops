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

internal delegate ValueTask ProcessPolicyFragmentsToPut(CancellationToken cancellationToken);
internal delegate ValueTask ProcessDeletedPolicyFragments(CancellationToken cancellationToken);

internal delegate Option<PublisherAction> FindPolicyFragmentAction(FileInfo file);

file delegate Option<PolicyFragmentName> TryParsePolicyFragmentName(FileInfo file);

file delegate ValueTask ProcessPolicyFragment(PolicyFragmentName name, CancellationToken cancellationToken);

file delegate bool IsPolicyFragmentNameInSourceControl(PolicyFragmentName name);

file delegate ValueTask<Option<PolicyFragmentDto>> FindPolicyFragmentDto(PolicyFragmentName name, CancellationToken cancellationToken);

internal delegate ValueTask PutPolicyFragment(PolicyFragmentName name, CancellationToken cancellationToken);

file delegate ValueTask DeletePolicyFragment(PolicyFragmentName name, CancellationToken cancellationToken);

file delegate ValueTask PutPolicyFragmentInApim(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeletePolicyFragmentFromApim(PolicyFragmentName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingPolicyFragment(PolicyFragmentName name, CancellationToken cancellationToken);

file sealed class ProcessPolicyFragmentsToPutHandler(GetPublisherFiles getPublisherFiles,
                                                     TryParsePolicyFragmentName tryParsePolicyFragmentName,
                                                     IsPolicyFragmentNameInSourceControl isNameInSourceControl,
                                                     PutPolicyFragment putPolicyFragment)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await getPublisherFiles()
                .Choose(tryParsePolicyFragmentName.Invoke)
                .Where(isNameInSourceControl.Invoke)
                .IterParallel(putPolicyFragment.Invoke, cancellationToken);
}

file sealed class ProcessDeletedPolicyFragmentsHandler(GetPublisherFiles getPublisherFiles,
                                                       TryParsePolicyFragmentName tryParsePolicyFragmentName,
                                                       IsPolicyFragmentNameInSourceControl isNameInSourceControl,
                                                       DeletePolicyFragment deletePolicyFragment)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await getPublisherFiles()
                .Choose(tryParsePolicyFragmentName.Invoke)
                .Where(name => isNameInSourceControl.Invoke(name) is false)
                .IterParallel(deletePolicyFragment.Invoke, cancellationToken);
}

file sealed class FindPolicyFragmentActionHandler(TryParsePolicyFragmentName tryParseName, ProcessPolicyFragment processPolicyFragment)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(PolicyFragmentName name) =>
        async cancellationToken => await processPolicyFragment(name, cancellationToken);
}

file sealed class TryParsePolicyFragmentNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<PolicyFragmentName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file)
        | TryParseNameFromPolicyFile(file);

    private Option<PolicyFragmentName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in PolicyFragmentInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;

    private Option<PolicyFragmentName> TryParseNameFromPolicyFile(FileInfo file) =>
        from policyFile in PolicyFragmentPolicyFile.TryParse(file, serviceDirectory)
        select policyFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class PolicyFragmentSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<PolicyFragmentName> locker = new(LockOptions.Default);
    private ImmutableHashSet<PolicyFragmentName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(PolicyFragmentName name, Func<PolicyFragmentName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessPolicyFragmentHandler(IsPolicyFragmentNameInSourceControl isNameInSourceControl, PutPolicyFragment put, DeletePolicyFragment delete) : IDisposable
{
    private readonly PolicyFragmentSemaphore semaphore = new();

    public async ValueTask Handle(PolicyFragmentName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(PolicyFragmentName name, CancellationToken cancellationToken)
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

file sealed class IsPolicyFragmentNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(PolicyFragmentName name) =>
        DoesInformationFileExist(name)
        || DoesPolicyFileExist(name);

    private bool DoesInformationFileExist(PolicyFragmentName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = PolicyFragmentInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }

    private bool DoesPolicyFileExist(PolicyFragmentName name)
    {
        var artifactFiles = getArtifactFiles();
        var policyFile = PolicyFragmentPolicyFile.From(name, serviceDirectory);

        return artifactFiles.Contains(policyFile.ToFileInfo());
    }
}

file sealed class PutPolicyFragmentHandler(FindPolicyFragmentDto findDto, PutPolicyFragmentInApim putInApim) : IDisposable
{
    private readonly PolicyFragmentSemaphore semaphore = new();

    public async ValueTask Handle(PolicyFragmentName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(PolicyFragmentName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindPolicyFragmentDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<PolicyFragmentDto>> Handle(PolicyFragmentName name, CancellationToken cancellationToken)
    {
        var informationFileDtoOption = await TryGetInformationFileDto(name, cancellationToken);
        var policyContentsOption = await TryGetPolicyContents(name, cancellationToken);

        return TryGetDto(name, informationFileDtoOption, policyContentsOption);
    }

    private async ValueTask<Option<PolicyFragmentDto>> TryGetInformationFileDto(PolicyFragmentName name, CancellationToken cancellationToken)
    {
        var informationFile = PolicyFragmentInformationFile.From(name, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<PolicyFragmentDto>();
    }

    private async ValueTask<Option<BinaryData>> TryGetPolicyContents(PolicyFragmentName name, CancellationToken cancellationToken)
    {
        var policyFile = PolicyFragmentPolicyFile.From(name, serviceDirectory);

        return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
    }

    private Option<PolicyFragmentDto> TryGetDto(PolicyFragmentName name, Option<PolicyFragmentDto> informationFileDtoOption, Option<BinaryData> policyContentsOption)
    {
        if (informationFileDtoOption.IsNone && policyContentsOption.IsNone)
        {
            return Option<PolicyFragmentDto>.None;
        }

        var dto = informationFileDtoOption.IfNone(() => new PolicyFragmentDto { Properties = new PolicyFragmentDto.PolicyFragmentContract() });
        policyContentsOption.Iter(contents => dto = dto with
        {
            Properties = dto.Properties with
            {
                Format = "rawxml",
                Value = contents.ToString()
            }
        });

        var overrideDto = overrideFactory.Create<PolicyFragmentName, PolicyFragmentDto>();

        return overrideDto(name, dto);
    }
}

file sealed class PutPolicyFragmentInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(PolicyFragmentName name, PolicyFragmentDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy fragment {PolicyFragmentName}...", name);
        await PolicyFragmentUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeletePolicyFragmentHandler(IEnumerable<OnDeletingPolicyFragment> onDeletingHandlers, DeletePolicyFragmentFromApim deleteFromApim) : IDisposable
{
    private readonly PolicyFragmentSemaphore semaphore = new();

    public async ValueTask Handle(PolicyFragmentName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(PolicyFragmentName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeletePolicyFragmentFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(PolicyFragmentName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy fragment {PolicyFragmentName}...", name);
        await PolicyFragmentUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class PolicyFragmentServices
{
    public static void ConfigureProcessPolicyFragmentsToPut(IServiceCollection services)
    {
        ConfigureTryParsePolicyFragmentName(services);
        ConfigureIsPolicyFragmentNameInSourceControl(services);
        ConfigurePutPolicyFragment(services);

        services.TryAddSingleton<ProcessPolicyFragmentsToPutHandler>();
        services.TryAddSingleton<ProcessPolicyFragmentsToPut>(provider => provider.GetRequiredService<ProcessPolicyFragmentsToPutHandler>().Handle);
    }

    public static void ConfigureProcessDeletedPolicyFragments(IServiceCollection services)
    {
        ConfigureTryParsePolicyFragmentName(services);
        ConfigureIsPolicyFragmentNameInSourceControl(services);
        ConfigureDeletePolicyFragment(services);

        services.TryAddSingleton<ProcessDeletedPolicyFragmentsHandler>();
        services.TryAddSingleton<ProcessDeletedPolicyFragments>(provider => provider.GetRequiredService<ProcessDeletedPolicyFragmentsHandler>().Handle);
    }

    public static void ConfigureFindPolicyFragmentAction(IServiceCollection services)
    {
        ConfigureTryParsePolicyFragmentName(services);
        ConfigureProcessPolicyFragment(services);

        services.TryAddSingleton<FindPolicyFragmentActionHandler>();
        services.TryAddSingleton<FindPolicyFragmentAction>(provider => provider.GetRequiredService<FindPolicyFragmentActionHandler>().Handle);
    }

    private static void ConfigureTryParsePolicyFragmentName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParsePolicyFragmentNameHandler>();
        services.TryAddSingleton<TryParsePolicyFragmentName>(provider => provider.GetRequiredService<TryParsePolicyFragmentNameHandler>().Handle);
    }

    private static void ConfigureProcessPolicyFragment(IServiceCollection services)
    {
        ConfigureIsPolicyFragmentNameInSourceControl(services);
        ConfigurePutPolicyFragment(services);
        ConfigureDeletePolicyFragment(services);

        services.TryAddSingleton<ProcessPolicyFragmentHandler>();
        services.TryAddSingleton<ProcessPolicyFragment>(provider => provider.GetRequiredService<ProcessPolicyFragmentHandler>().Handle);
    }

    private static void ConfigureIsPolicyFragmentNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsPolicyFragmentNameInSourceControlHandler>();
        services.TryAddSingleton<IsPolicyFragmentNameInSourceControl>(provider => provider.GetRequiredService<IsPolicyFragmentNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutPolicyFragment(IServiceCollection services)
    {
        ConfigureFindPolicyFragmentDto(services);
        ConfigurePutPolicyFragmentInApim(services);

        services.TryAddSingleton<PutPolicyFragmentHandler>();
        services.TryAddSingleton<PutPolicyFragment>(provider => provider.GetRequiredService<PutPolicyFragmentHandler>().Handle);
    }

    private static void ConfigureFindPolicyFragmentDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindPolicyFragmentDtoHandler>();
        services.TryAddSingleton<FindPolicyFragmentDto>(provider => provider.GetRequiredService<FindPolicyFragmentDtoHandler>().Handle);
    }

    private static void ConfigurePutPolicyFragmentInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutPolicyFragmentInApimHandler>();
        services.TryAddSingleton<PutPolicyFragmentInApim>(provider => provider.GetRequiredService<PutPolicyFragmentInApimHandler>().Handle);
    }

    private static void ConfigureDeletePolicyFragment(IServiceCollection services)
    {
        ConfigureOnDeletingPolicyFragment(services);
        ConfigureDeletePolicyFragmentFromApim(services);

        services.TryAddSingleton<DeletePolicyFragmentHandler>();
        services.TryAddSingleton<DeletePolicyFragment>(provider => provider.GetRequiredService<DeletePolicyFragmentHandler>().Handle);
    }

    private static void ConfigureOnDeletingPolicyFragment(IServiceCollection services)
    {
    }

    private static void ConfigureDeletePolicyFragmentFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeletePolicyFragmentFromApimHandler>();
        services.TryAddSingleton<DeletePolicyFragmentFromApim>(provider => provider.GetRequiredService<DeletePolicyFragmentFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("PolicyFragmentPublisher");
}