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

internal delegate Option<PublisherAction> FindServicePolicyAction(FileInfo file);

file delegate Option<ServicePolicyName> TryParseServicePolicyName(FileInfo file);

file delegate ValueTask ProcessServicePolicy(ServicePolicyName name, CancellationToken cancellationToken);

file delegate bool IsServicePolicyNameInSourceControl(ServicePolicyName name);

file delegate ValueTask<Option<ServicePolicyDto>> FindServicePolicyDto(ServicePolicyName name, CancellationToken cancellationToken);

internal delegate ValueTask PutServicePolicy(ServicePolicyName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteServicePolicy(ServicePolicyName name, CancellationToken cancellationToken);

file delegate ValueTask PutServicePolicyInApim(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteServicePolicyFromApim(ServicePolicyName name, CancellationToken cancellationToken);

file sealed class FindServicePolicyActionHandler(TryParseServicePolicyName tryParseName, ProcessServicePolicy processServicePolicy)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(ServicePolicyName name) =>
        async cancellationToken => await processServicePolicy(name, cancellationToken);
}

file sealed class TryParseServicePolicyNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<ServicePolicyName> Handle(FileInfo file) =>
        TryParseNameFromPolicyFile(file);

    private Option<ServicePolicyName> TryParseNameFromPolicyFile(FileInfo file) =>
        from policyFile in ServicePolicyFile.TryParse(file, serviceDirectory)
        select policyFile.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ServicePolicySemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<ServicePolicyName> locker = new(LockOptions.Default);
    private ImmutableHashSet<ServicePolicyName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ServicePolicyName name, Func<ServicePolicyName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessServicePolicyHandler(IsServicePolicyNameInSourceControl isNameInSourceControl, PutServicePolicy put, DeleteServicePolicy delete) : IDisposable
{
    private readonly ServicePolicySemaphore semaphore = new();

    public async ValueTask Handle(ServicePolicyName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ServicePolicyName name, CancellationToken cancellationToken)
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

file sealed class IsServicePolicyNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ServicePolicyName name) =>
        DoesPolicyFileExist(name);

    private bool DoesPolicyFileExist(ServicePolicyName name)
    {
        var artifactFiles = getArtifactFiles();
        var policyFile = ServicePolicyFile.From(name, serviceDirectory);

        return artifactFiles.Contains(policyFile.ToFileInfo());
    }
}

file sealed class PutServicePolicyHandler(FindServicePolicyDto findDto, PutServicePolicyInApim putInApim) : IDisposable
{
    private readonly ServicePolicySemaphore semaphore = new();

    public async ValueTask Handle(ServicePolicyName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(ServicePolicyName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindServicePolicyDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<ServicePolicyDto>> Handle(ServicePolicyName name, CancellationToken cancellationToken)
    {
        var contentsOption = await TryGetPolicyContents(name, cancellationToken);

        return from contents in contentsOption
               let dto = new ServicePolicyDto
               {
                   Properties = new ServicePolicyDto.ServicePolicyContract
                   {
                       Format = "rawxml",
                       Value = contents.ToString()
                   }
               }
               let overrideDto = overrideFactory.Create<ServicePolicyName, ServicePolicyDto>()
               select overrideDto(name, dto);
    }

    private async ValueTask<Option<BinaryData>> TryGetPolicyContents(ServicePolicyName name, CancellationToken cancellationToken)
    {
        var policyFile = ServicePolicyFile.From(name, serviceDirectory);

        return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
    }
}

file sealed class PutServicePolicyInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting service policy {ServicePolicyName}...", name);
        await ServicePolicyUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteServicePolicyHandler(DeleteServicePolicyFromApim deleteFromApim) : IDisposable
{
    private readonly ServicePolicySemaphore semaphore = new();

    public async ValueTask Handle(ServicePolicyName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(ServicePolicyName name, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteServicePolicyFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ServicePolicyName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting service policy {ServicePolicyName}...", name);
        await ServicePolicyUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ServicePolicyServices
{
    public static void ConfigureFindServicePolicyAction(IServiceCollection services)
    {
        ConfigureTryParseServicePolicyName(services);
        ConfigureProcessServicePolicy(services);

        services.TryAddSingleton<FindServicePolicyActionHandler>();
        services.TryAddSingleton<FindServicePolicyAction>(provider => provider.GetRequiredService<FindServicePolicyActionHandler>().Handle);
    }

    private static void ConfigureTryParseServicePolicyName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseServicePolicyNameHandler>();
        services.TryAddSingleton<TryParseServicePolicyName>(provider => provider.GetRequiredService<TryParseServicePolicyNameHandler>().Handle);
    }

    private static void ConfigureProcessServicePolicy(IServiceCollection services)
    {
        ConfigureIsServicePolicyNameInSourceControl(services);
        ConfigurePutServicePolicy(services);
        ConfigureDeleteServicePolicy(services);

        services.TryAddSingleton<ProcessServicePolicyHandler>();
        services.TryAddSingleton<ProcessServicePolicy>(provider => provider.GetRequiredService<ProcessServicePolicyHandler>().Handle);
    }

    private static void ConfigureIsServicePolicyNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsServicePolicyNameInSourceControlHandler>();
        services.TryAddSingleton<IsServicePolicyNameInSourceControl>(provider => provider.GetRequiredService<IsServicePolicyNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutServicePolicy(IServiceCollection services)
    {
        ConfigureFindServicePolicyDto(services);
        ConfigurePutServicePolicyInApim(services);

        services.TryAddSingleton<PutServicePolicyHandler>();
        services.TryAddSingleton<PutServicePolicy>(provider => provider.GetRequiredService<PutServicePolicyHandler>().Handle);
    }

    private static void ConfigureFindServicePolicyDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindServicePolicyDtoHandler>();
        services.TryAddSingleton<FindServicePolicyDto>(provider => provider.GetRequiredService<FindServicePolicyDtoHandler>().Handle);
    }

    private static void ConfigurePutServicePolicyInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutServicePolicyInApimHandler>();
        services.TryAddSingleton<PutServicePolicyInApim>(provider => provider.GetRequiredService<PutServicePolicyInApimHandler>().Handle);
    }

    private static void ConfigureDeleteServicePolicy(IServiceCollection services)
    {
        ConfigureDeleteServicePolicyFromApim(services);

        services.TryAddSingleton<DeleteServicePolicyHandler>();
        services.TryAddSingleton<DeleteServicePolicy>(provider => provider.GetRequiredService<DeleteServicePolicyHandler>().Handle);
    }

    private static void ConfigureDeleteServicePolicyFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteServicePolicyFromApimHandler>();
        services.TryAddSingleton<DeleteServicePolicyFromApim>(provider => provider.GetRequiredService<DeleteServicePolicyFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ServicePolicyPublisher");
}