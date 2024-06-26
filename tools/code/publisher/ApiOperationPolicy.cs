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

internal delegate Option<PublisherAction> FindApiOperationPolicyAction(FileInfo file);

file delegate Option<(ApiOperationPolicyName Name, ApiOperationName ApiOperationName, ApiName ApiName)> TryParseApiOperationPolicyName(FileInfo file);

file delegate ValueTask ProcessApiOperationPolicy(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate bool IsApiOperationPolicyNameInSourceControl(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName);

file delegate ValueTask<Option<ApiOperationPolicyDto>> FindApiOperationPolicyDto(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask PutApiOperationPolicy(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiOperationPolicy(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask PutApiOperationPolicyInApim(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiOperationPolicyFromApim(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

file sealed class FindApiOperationPolicyActionHandler(TryParseApiOperationPolicyName tryParseName, ProcessApiOperationPolicy processApiOperationPolicy)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ApiOperationName, names.ApiName);

    private PublisherAction GetAction(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName) =>
        async cancellationToken => await processApiOperationPolicy(name, apiOperationName, apiName, cancellationToken);
}

file sealed class TryParseApiOperationPolicyNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(ApiOperationPolicyName, ApiOperationName, ApiName)> Handle(FileInfo file) =>
        TryParseNameFromPolicyFile(file);

    private Option<(ApiOperationPolicyName, ApiOperationName, ApiName)> TryParseNameFromPolicyFile(FileInfo file) =>
        from policyFile in ApiOperationPolicyFile.TryParse(file, serviceDirectory)
        select (policyFile.Name, policyFile.Parent.Name, policyFile.Parent.Parent.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ApiOperationPolicySemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(ApiOperationPolicyName, ApiOperationName, ApiName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(ApiOperationPolicyName, ApiOperationName, ApiName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, Func<ApiOperationPolicyName, ApiOperationName, ApiName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync((name, apiOperationName, apiName), cancellationToken).ConfigureAwait(false);

        // Only process each name once
        if (processedNames.Contains((name, apiOperationName, apiName)))
        {
            return;
        }

        await action(name, apiOperationName, apiName, cancellationToken);

        ImmutableInterlocked.Update(ref processedNames, set => set.Add((name, apiOperationName, apiName)));
    }

    public void Dispose() => locker.Dispose();
}

file sealed class ProcessApiOperationPolicyHandler(IsApiOperationPolicyNameInSourceControl isNameInSourceControl, PutApiOperationPolicy put, DeleteApiOperationPolicy delete) : IDisposable
{
    private readonly ApiOperationPolicySemaphore semaphore = new();

    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiOperationName, apiName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        if (isNameInSourceControl(name, apiOperationName, apiName))
        {
            await put(name, apiOperationName, apiName, cancellationToken);
        }
        else
        {
            await delete(name, apiOperationName, apiName, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class IsApiOperationPolicyNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName) =>
        DoesPolicyFileExist(name, apiOperationName, apiName);

    private bool DoesPolicyFileExist(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName)
    {
        var artifactFiles = getArtifactFiles();
        var policyFile = ApiOperationPolicyFile.From(name, apiOperationName, apiName, serviceDirectory);

        return artifactFiles.Contains(policyFile.ToFileInfo());
    }
}

file sealed class PutApiOperationPolicyHandler(FindApiOperationPolicyDto findDto, PutApi putApi, PutApiOperationPolicyInApim putInApim) : IDisposable
{
    private readonly ApiOperationPolicySemaphore semaphore = new();

    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiOperationName, apiName, Put, cancellationToken);

    private async ValueTask Put(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, apiOperationName, apiName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, apiOperationName, apiName, cancellationToken));
    }

    private async ValueTask Put(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        await putApi(apiName, cancellationToken);
        await putInApim(name, dto, apiOperationName, apiName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindApiOperationPolicyDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ApiOperationPolicyDto>> Handle(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        var contentsOption = await TryGetPolicyContents(name, apiOperationName, apiName, cancellationToken);

        return from contents in contentsOption
               select new ApiOperationPolicyDto
               {
                   Properties = new ApiOperationPolicyDto.ApiOperationPolicyContract
                   {
                       Format = "rawxml",
                       Value = contents.ToString()
                   }
               };
    }

    private async ValueTask<Option<BinaryData>> TryGetPolicyContents(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        var policyFile = ApiOperationPolicyFile.From(name, apiOperationName, apiName, serviceDirectory);

        return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
    }
}

file sealed class PutApiOperationPolicyInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy {ApiOperationPolicyName} for operation {ApiOperationName} in API {ApiName}...", name, apiOperationName, apiName);
        await ApiOperationPolicyUri.From(name, apiOperationName, apiName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteApiOperationPolicyHandler(DeleteApiOperationPolicyFromApim deleteFromApim) : IDisposable
{
    private readonly ApiOperationPolicySemaphore semaphore = new();

    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiOperationName, apiName, Delete, cancellationToken);

    private async ValueTask Delete(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, apiOperationName, apiName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteApiOperationPolicyFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiOperationPolicyName name, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy {ApiOperationPolicyName} from operation {ApiOperationName} in API {Apiname}...", name, apiOperationName, apiName);
        await ApiOperationPolicyUri.From(name, apiOperationName, apiName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ApiOperationPolicyServices
{
    public static void ConfigureFindApiOperationPolicyAction(IServiceCollection services)
    {
        ConfigureTryParseApiOperationPolicyName(services);
        ConfigureProcessApiOperationPolicy(services);

        services.TryAddSingleton<FindApiOperationPolicyActionHandler>();
        services.TryAddSingleton<FindApiOperationPolicyAction>(provider => provider.GetRequiredService<FindApiOperationPolicyActionHandler>().Handle);
    }

    private static void ConfigureTryParseApiOperationPolicyName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseApiOperationPolicyNameHandler>();
        services.TryAddSingleton<TryParseApiOperationPolicyName>(provider => provider.GetRequiredService<TryParseApiOperationPolicyNameHandler>().Handle);
    }

    private static void ConfigureProcessApiOperationPolicy(IServiceCollection services)
    {
        ConfigureIsApiOperationPolicyNameInSourceControl(services);
        ConfigurePutApiOperationPolicy(services);
        ConfigureDeleteApiOperationPolicy(services);

        services.TryAddSingleton<ProcessApiOperationPolicyHandler>();
        services.TryAddSingleton<ProcessApiOperationPolicy>(provider => provider.GetRequiredService<ProcessApiOperationPolicyHandler>().Handle);
    }

    private static void ConfigureIsApiOperationPolicyNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsApiOperationPolicyNameInSourceControlHandler>();
        services.TryAddSingleton<IsApiOperationPolicyNameInSourceControl>(provider => provider.GetRequiredService<IsApiOperationPolicyNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutApiOperationPolicy(IServiceCollection services)
    {
        ConfigureFindApiOperationPolicyDto(services);
        ConfigurePutApiOperationPolicyInApim(services);
        ApiServices.ConfigurePutApi(services);

        services.TryAddSingleton<PutApiOperationPolicyHandler>();
        services.TryAddSingleton<PutApiOperationPolicy>(provider => provider.GetRequiredService<PutApiOperationPolicyHandler>().Handle);
    }

    private static void ConfigureFindApiOperationPolicyDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindApiOperationPolicyDtoHandler>();
        services.TryAddSingleton<FindApiOperationPolicyDto>(provider => provider.GetRequiredService<FindApiOperationPolicyDtoHandler>().Handle);
    }

    private static void ConfigurePutApiOperationPolicyInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutApiOperationPolicyInApimHandler>();
        services.TryAddSingleton<PutApiOperationPolicyInApim>(provider => provider.GetRequiredService<PutApiOperationPolicyInApimHandler>().Handle);
    }

    private static void ConfigureDeleteApiOperationPolicy(IServiceCollection services)
    {
        ConfigureDeleteApiOperationPolicyFromApim(services);

        services.TryAddSingleton<DeleteApiOperationPolicyHandler>();
        services.TryAddSingleton<DeleteApiOperationPolicy>(provider => provider.GetRequiredService<DeleteApiOperationPolicyHandler>().Handle);
    }

    private static void ConfigureDeleteApiOperationPolicyFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteApiOperationPolicyFromApimHandler>();
        services.TryAddSingleton<DeleteApiOperationPolicyFromApim>(provider => provider.GetRequiredService<DeleteApiOperationPolicyFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ApiOperationPolicyPublisher");
}