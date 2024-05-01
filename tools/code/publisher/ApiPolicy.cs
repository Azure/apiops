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

internal delegate Option<PublisherAction> FindApiPolicyAction(FileInfo file);

file delegate Option<(ApiPolicyName Name, ApiName ApiName)> TryParseApiPolicyName(FileInfo file);

file delegate ValueTask ProcessApiPolicy(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);

file delegate bool IsApiPolicyNameInSourceControl(ApiPolicyName name, ApiName apiName);

file delegate ValueTask<Option<ApiPolicyDto>> FindApiPolicyDto(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask PutApiPolicy(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiPolicy(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask PutApiPolicyInApim(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiPolicyFromApim(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken);

file sealed class FindApiPolicyActionHandler(TryParseApiPolicyName tryParseName, ProcessApiPolicy processApiPolicy)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ApiName);

    private PublisherAction GetAction(ApiPolicyName name, ApiName apiName) =>
        async cancellationToken => await processApiPolicy(name, apiName, cancellationToken);
}

file sealed class TryParseApiPolicyNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(ApiPolicyName, ApiName)> Handle(FileInfo file) =>
        TryParseNameFromPolicyFile(file);

    private Option<(ApiPolicyName, ApiName)> TryParseNameFromPolicyFile(FileInfo file) =>
        from policyFile in ApiPolicyFile.TryParse(file, serviceDirectory)
        select (policyFile.Name, policyFile.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ApiPolicySemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(ApiPolicyName, ApiName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(ApiPolicyName, ApiName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ApiPolicyName name, ApiName apiName, Func<ApiPolicyName, ApiName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync((name, apiName), cancellationToken).ConfigureAwait(false);

        // Only process each name once
        if (processedNames.Contains((name, apiName)))
        {
            return;
        }

        await action(name, apiName, cancellationToken);

        ImmutableInterlocked.Update(ref processedNames, set => set.Add((name, apiName)));
    }

    public void Dispose() => locker.Dispose();
}

file sealed class ProcessApiPolicyHandler(IsApiPolicyNameInSourceControl isNameInSourceControl, PutApiPolicy put, DeleteApiPolicy delete) : IDisposable
{
    private readonly ApiPolicySemaphore semaphore = new();

    public async ValueTask Handle(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
    {
        if (isNameInSourceControl(name, apiName))
        {
            await put(name, apiName, cancellationToken);
        }
        else
        {
            await delete(name, apiName, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class IsApiPolicyNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ApiPolicyName name, ApiName apiName) =>
        DoesPolicyFileExist(name, apiName);

    private bool DoesPolicyFileExist(ApiPolicyName name, ApiName apiName)
    {
        var artifactFiles = getArtifactFiles();
        var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

        return artifactFiles.Contains(policyFile.ToFileInfo());
    }
}

file sealed class PutApiPolicyHandler(FindApiPolicyDto findDto, PutApi putApi, PutApiPolicyInApim putInApim) : IDisposable
{
    private readonly ApiPolicySemaphore semaphore = new();

    public async ValueTask Handle(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiName, Put, cancellationToken);

    private async ValueTask Put(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, apiName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, apiName, cancellationToken));
    }

    private async ValueTask Put(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        await putApi(apiName, cancellationToken);
        await putInApim(name, dto, apiName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindApiPolicyDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ApiPolicyDto>> Handle(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
    {
        var contentsOption = await TryGetPolicyContents(name, apiName, cancellationToken);

        return from contents in contentsOption
               select new ApiPolicyDto
               {
                   Properties = new ApiPolicyDto.ApiPolicyContract
                   {
                       Format = "rawxml",
                       Value = contents.ToString()
                   }
               };
    }

    private async ValueTask<Option<BinaryData>> TryGetPolicyContents(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
    {
        var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

        return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
    }
}

file sealed class PutApiPolicyInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting policy {ApiPolicyName} for API {ApiName}...", name, apiName);
        await ApiPolicyUri.From(name, apiName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteApiPolicyHandler(DeleteApiPolicyFromApim deleteFromApim) : IDisposable
{
    private readonly ApiPolicySemaphore semaphore = new();

    public async ValueTask Handle(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiName, Delete, cancellationToken);

    private async ValueTask Delete(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, apiName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteApiPolicyFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiPolicyName name, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting policy {ApiPolicyName} from API {ApiName}...", name, apiName);
        await ApiPolicyUri.From(name, apiName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ApiPolicyServices
{
    public static void ConfigureFindApiPolicyAction(IServiceCollection services)
    {
        ConfigureTryParseApiPolicyName(services);
        ConfigureProcessApiPolicy(services);

        services.TryAddSingleton<FindApiPolicyActionHandler>();
        services.TryAddSingleton<FindApiPolicyAction>(provider => provider.GetRequiredService<FindApiPolicyActionHandler>().Handle);
    }

    private static void ConfigureTryParseApiPolicyName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseApiPolicyNameHandler>();
        services.TryAddSingleton<TryParseApiPolicyName>(provider => provider.GetRequiredService<TryParseApiPolicyNameHandler>().Handle);
    }

    private static void ConfigureProcessApiPolicy(IServiceCollection services)
    {
        ConfigureIsApiPolicyNameInSourceControl(services);
        ConfigurePutApiPolicy(services);
        ConfigureDeleteApiPolicy(services);

        services.TryAddSingleton<ProcessApiPolicyHandler>();
        services.TryAddSingleton<ProcessApiPolicy>(provider => provider.GetRequiredService<ProcessApiPolicyHandler>().Handle);
    }

    private static void ConfigureIsApiPolicyNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsApiPolicyNameInSourceControlHandler>();
        services.TryAddSingleton<IsApiPolicyNameInSourceControl>(provider => provider.GetRequiredService<IsApiPolicyNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutApiPolicy(IServiceCollection services)
    {
        ConfigureFindApiPolicyDto(services);
        ConfigurePutApiPolicyInApim(services);
        ApiServices.ConfigurePutApi(services);

        services.TryAddSingleton<PutApiPolicyHandler>();
        services.TryAddSingleton<PutApiPolicy>(provider => provider.GetRequiredService<PutApiPolicyHandler>().Handle);
    }

    private static void ConfigureFindApiPolicyDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindApiPolicyDtoHandler>();
        services.TryAddSingleton<FindApiPolicyDto>(provider => provider.GetRequiredService<FindApiPolicyDtoHandler>().Handle);
    }

    private static void ConfigurePutApiPolicyInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutApiPolicyInApimHandler>();
        services.TryAddSingleton<PutApiPolicyInApim>(provider => provider.GetRequiredService<PutApiPolicyInApimHandler>().Handle);
    }

    private static void ConfigureDeleteApiPolicy(IServiceCollection services)
    {
        ConfigureDeleteApiPolicyFromApim(services);

        services.TryAddSingleton<DeleteApiPolicyHandler>();
        services.TryAddSingleton<DeleteApiPolicy>(provider => provider.GetRequiredService<DeleteApiPolicyHandler>().Handle);
    }

    private static void ConfigureDeleteApiPolicyFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteApiPolicyFromApimHandler>();
        services.TryAddSingleton<DeleteApiPolicyFromApim>(provider => provider.GetRequiredService<DeleteApiPolicyFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ApiPolicyPublisher");
}