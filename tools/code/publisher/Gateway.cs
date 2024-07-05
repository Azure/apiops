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

internal delegate Option<PublisherAction> FindGatewayAction(FileInfo file);

file delegate Option<GatewayName> TryParseGatewayName(FileInfo file);

file delegate ValueTask ProcessGateway(GatewayName name, CancellationToken cancellationToken);

file delegate bool IsGatewayNameInSourceControl(GatewayName name);

file delegate ValueTask<Option<GatewayDto>> FindGatewayDto(GatewayName name, CancellationToken cancellationToken);

internal delegate ValueTask PutGateway(GatewayName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteGateway(GatewayName name, CancellationToken cancellationToken);

file delegate ValueTask PutGatewayInApim(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteGatewayFromApim(GatewayName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingGateway(GatewayName name, CancellationToken cancellationToken);

file sealed class FindGatewayActionHandler(TryParseGatewayName tryParseName, ProcessGateway processGateway)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(GatewayName name) =>
        async cancellationToken => await processGateway(name, cancellationToken);
}

file sealed class TryParseGatewayNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<GatewayName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<GatewayName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in GatewayInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class GatewaySemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<GatewayName> locker = new(LockOptions.Default);
    private ImmutableHashSet<GatewayName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(GatewayName name, Func<GatewayName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessGatewayHandler(IsGatewayNameInSourceControl isNameInSourceControl, PutGateway put, DeleteGateway delete) : IDisposable
{
    private readonly GatewaySemaphore semaphore = new();

    public async ValueTask Handle(GatewayName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(GatewayName name, CancellationToken cancellationToken)
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

file sealed class IsGatewayNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(GatewayName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(GatewayName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = GatewayInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutGatewayHandler(FindGatewayDto findDto, PutGatewayInApim putInApim) : IDisposable
{
    private readonly GatewaySemaphore semaphore = new();

    public async ValueTask Handle(GatewayName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(GatewayName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindGatewayDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<GatewayDto>> Handle(GatewayName name, CancellationToken cancellationToken)
    {
        var informationFile = GatewayInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<GatewayDto>()
               let overrideDto = overrideFactory.Create<GatewayName, GatewayDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutGatewayInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GatewayName name, GatewayDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting gateway {GatewayName}...", name);
        await GatewayUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteGatewayHandler(IEnumerable<OnDeletingGateway> onDeletingHandlers, DeleteGatewayFromApim deleteFromApim) : IDisposable
{
    private readonly GatewaySemaphore semaphore = new();

    public async ValueTask Handle(GatewayName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(GatewayName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteGatewayFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GatewayName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting gateway {GatewayName}...", name);
        await GatewayUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class GatewayServices
{
    public static void ConfigureFindGatewayAction(IServiceCollection services)
    {
        ConfigureTryParseGatewayName(services);
        ConfigureProcessGateway(services);

        services.TryAddSingleton<FindGatewayActionHandler>();
        services.TryAddSingleton<FindGatewayAction>(provider => provider.GetRequiredService<FindGatewayActionHandler>().Handle);
    }

    private static void ConfigureTryParseGatewayName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseGatewayNameHandler>();
        services.TryAddSingleton<TryParseGatewayName>(provider => provider.GetRequiredService<TryParseGatewayNameHandler>().Handle);
    }

    private static void ConfigureProcessGateway(IServiceCollection services)
    {
        ConfigureIsGatewayNameInSourceControl(services);
        ConfigurePutGateway(services);
        ConfigureDeleteGateway(services);

        services.TryAddSingleton<ProcessGatewayHandler>();
        services.TryAddSingleton<ProcessGateway>(provider => provider.GetRequiredService<ProcessGatewayHandler>().Handle);
    }

    private static void ConfigureIsGatewayNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsGatewayNameInSourceControlHandler>();
        services.TryAddSingleton<IsGatewayNameInSourceControl>(provider => provider.GetRequiredService<IsGatewayNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutGateway(IServiceCollection services)
    {
        ConfigureFindGatewayDto(services);
        ConfigurePutGatewayInApim(services);

        services.TryAddSingleton<PutGatewayHandler>();
        services.TryAddSingleton<PutGateway>(provider => provider.GetRequiredService<PutGatewayHandler>().Handle);
    }

    private static void ConfigureFindGatewayDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindGatewayDtoHandler>();
        services.TryAddSingleton<FindGatewayDto>(provider => provider.GetRequiredService<FindGatewayDtoHandler>().Handle);
    }

    private static void ConfigurePutGatewayInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutGatewayInApimHandler>();
        services.TryAddSingleton<PutGatewayInApim>(provider => provider.GetRequiredService<PutGatewayInApimHandler>().Handle);
    }

    private static void ConfigureDeleteGateway(IServiceCollection services)
    {
        ConfigureOnDeletingGateway(services);
        ConfigureDeleteGatewayFromApim(services);

        services.TryAddSingleton<DeleteGatewayHandler>();
        services.TryAddSingleton<DeleteGateway>(provider => provider.GetRequiredService<DeleteGatewayHandler>().Handle);
    }

    private static void ConfigureOnDeletingGateway(IServiceCollection services)
    {
    }

    private static void ConfigureDeleteGatewayFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteGatewayFromApimHandler>();
        services.TryAddSingleton<DeleteGatewayFromApim>(provider => provider.GetRequiredService<DeleteGatewayFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("GatewayPublisher");
}