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

internal delegate Option<PublisherAction> FindGatewayApiAction(FileInfo file);

file delegate Option<(ApiName Name, GatewayName GatewayName)> TryParseApiName(FileInfo file);

file delegate ValueTask ProcessGatewayApi(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);

file delegate bool IsApiNameInSourceControl(ApiName name, GatewayName gatewayName);

file delegate ValueTask<Option<GatewayApiDto>> FindGatewayApiDto(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);

internal delegate ValueTask PutGatewayApi(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);

file delegate ValueTask DeleteGatewayApi(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);

file delegate ValueTask PutGatewayApiInApim(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken);

file delegate ValueTask DeleteGatewayApiFromApim(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);

file sealed class FindGatewayApiActionHandler(TryParseApiName tryParseName, ProcessGatewayApi processGatewayApi)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.GatewayName);

    private PublisherAction GetAction(ApiName name, GatewayName gatewayName) =>
        async cancellationToken => await processGatewayApi(name, gatewayName, cancellationToken);
}

file sealed class TryParseApiNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(ApiName, GatewayName)> Handle(FileInfo file) =>
        TryParseNameFromApiInformationFile(file);

    private Option<(ApiName, GatewayName)> TryParseNameFromApiInformationFile(FileInfo file) =>
        from informationFile in GatewayApiInformationFile.TryParse(file, serviceDirectory)
        select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class GatewayApiSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(ApiName, GatewayName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(ApiName, GatewayName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(ApiName name, GatewayName gatewayName, Func<ApiName, GatewayName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
    {
        // Do not process the same name simultaneously
        using var _ = await locker.LockAsync((name, gatewayName), cancellationToken).ConfigureAwait(false);

        // Only process each name once
        if (processedNames.Contains((name, gatewayName)))
        {
            return;
        }

        await action(name, gatewayName, cancellationToken);

        ImmutableInterlocked.Update(ref processedNames, set => set.Add((name, gatewayName)));
    }

    public void Dispose() => locker.Dispose();
}

file sealed class ProcessGatewayApiHandler(IsApiNameInSourceControl isNameInSourceControl, PutGatewayApi put, DeleteGatewayApi delete) : IDisposable
{
    private readonly GatewayApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, gatewayName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        if (isNameInSourceControl(name, gatewayName))
        {
            await put(name, gatewayName, cancellationToken);
        }
        else
        {
            await delete(name, gatewayName, cancellationToken);
        }
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class IsApiNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(ApiName name, GatewayName gatewayName) =>
        DoesApiInformationFileExist(name, gatewayName);

    private bool DoesApiInformationFileExist(ApiName name, GatewayName gatewayName)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = GatewayApiInformationFile.From(name, gatewayName, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutGatewayApiHandler(FindGatewayApiDto findDto, PutGateway putGateway, PutApi putApi, PutGatewayApiInApim putInApim) : IDisposable
{
    private readonly GatewayApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, gatewayName, Put, cancellationToken);

    private async ValueTask Put(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, gatewayName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, gatewayName, cancellationToken));
    }

    private async ValueTask Put(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        await putGateway(gatewayName, cancellationToken);
        await putApi(name, cancellationToken);
        await putInApim(name, dto, gatewayName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindGatewayApiDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<GatewayApiDto>> Handle(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        var informationFile = GatewayApiInformationFile.From(name, gatewayName, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<GatewayApiDto>();
    }
}

file sealed class PutGatewayApiInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Adding api {ApiName} to gateway {GatewayName}...", name, gatewayName);
        await GatewayApiUri.From(name, gatewayName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteGatewayApiHandler(DeleteGatewayApiFromApim deleteFromApim) : IDisposable
{
    private readonly GatewayApiSemaphore semaphore = new();

    public async ValueTask Handle(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, gatewayName, Delete, cancellationToken);

    private async ValueTask Delete(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, gatewayName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteGatewayApiFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting api {ApiName} from gateway {GatewayName}...", name, gatewayName);
        await GatewayApiUri.From(name, gatewayName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class GatewayApiServices
{
    public static void ConfigureFindGatewayApiAction(IServiceCollection services)
    {
        ConfigureTryParseApiName(services);
        ConfigureProcessGatewayApi(services);

        services.TryAddSingleton<FindGatewayApiActionHandler>();
        services.TryAddSingleton<FindGatewayApiAction>(provider => provider.GetRequiredService<FindGatewayApiActionHandler>().Handle);
    }

    private static void ConfigureTryParseApiName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseApiNameHandler>();
        services.TryAddSingleton<TryParseApiName>(provider => provider.GetRequiredService<TryParseApiNameHandler>().Handle);
    }

    private static void ConfigureProcessGatewayApi(IServiceCollection services)
    {
        ConfigureIsApiNameInSourceControl(services);
        ConfigurePutGatewayApi(services);
        ConfigureDeleteGatewayApi(services);

        services.TryAddSingleton<ProcessGatewayApiHandler>();
        services.TryAddSingleton<ProcessGatewayApi>(provider => provider.GetRequiredService<ProcessGatewayApiHandler>().Handle);
    }

    private static void ConfigureIsApiNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsApiNameInSourceControlHandler>();
        services.TryAddSingleton<IsApiNameInSourceControl>(provider => provider.GetRequiredService<IsApiNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutGatewayApi(IServiceCollection services)
    {
        ConfigureFindGatewayApiDto(services);
        ConfigurePutGatewayApiInApim(services);
        GatewayServices.ConfigurePutGateway(services);
        ApiServices.ConfigurePutApi(services);

        services.TryAddSingleton<PutGatewayApiHandler>();
        services.TryAddSingleton<PutGatewayApi>(provider => provider.GetRequiredService<PutGatewayApiHandler>().Handle);
    }

    private static void ConfigureFindGatewayApiDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindGatewayApiDtoHandler>();
        services.TryAddSingleton<FindGatewayApiDto>(provider => provider.GetRequiredService<FindGatewayApiDtoHandler>().Handle);
    }

    private static void ConfigurePutGatewayApiInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutGatewayApiInApimHandler>();
        services.TryAddSingleton<PutGatewayApiInApim>(provider => provider.GetRequiredService<PutGatewayApiInApimHandler>().Handle);
    }

    private static void ConfigureDeleteGatewayApi(IServiceCollection services)
    {
        ConfigureDeleteGatewayApiFromApim(services);

        services.TryAddSingleton<DeleteGatewayApiHandler>();
        services.TryAddSingleton<DeleteGatewayApi>(provider => provider.GetRequiredService<DeleteGatewayApiHandler>().Handle);
    }

    private static void ConfigureDeleteGatewayApiFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteGatewayApiFromApimHandler>();
        services.TryAddSingleton<DeleteGatewayApiFromApim>(provider => provider.GetRequiredService<DeleteGatewayApiFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("GatewayApiPublisher");
}