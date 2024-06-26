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

internal delegate Option<PublisherAction> FindApiTagAction(FileInfo file);

file delegate Option<(TagName Name, ApiName ApiName)> TryParseTagName(FileInfo file);

file delegate ValueTask ProcessApiTag(TagName name, ApiName apiName, CancellationToken cancellationToken);

file delegate bool IsTagNameInSourceControl(TagName name, ApiName apiName);

file delegate ValueTask<Option<ApiTagDto>> FindApiTagDto(TagName name, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask PutApiTag(TagName name, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiTag(TagName name, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask PutApiTagInApim(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask DeleteApiTagFromApim(TagName name, ApiName apiName, CancellationToken cancellationToken);

file sealed class FindApiTagActionHandler(TryParseTagName tryParseName, ProcessApiTag processApiTag)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from names in tryParseName(file)
        select GetAction(names.Name, names.ApiName);

    private PublisherAction GetAction(TagName name, ApiName apiName) =>
        async cancellationToken => await processApiTag(name, apiName, cancellationToken);
}

file sealed class TryParseTagNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<(TagName, ApiName)> Handle(FileInfo file) =>
        TryParseNameFromTagInformationFile(file);

    private Option<(TagName, ApiName)> TryParseNameFromTagInformationFile(FileInfo file) =>
        from informationFile in ApiTagInformationFile.TryParse(file, serviceDirectory)
        select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class ApiTagSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<(TagName, ApiName)> locker = new(LockOptions.Default);
    private ImmutableHashSet<(TagName, ApiName)> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(TagName name, ApiName apiName, Func<TagName, ApiName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessApiTagHandler(IsTagNameInSourceControl isNameInSourceControl, PutApiTag put, DeleteApiTag delete) : IDisposable
{
    private readonly ApiTagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiName, HandleInner, cancellationToken);

    private async ValueTask HandleInner(TagName name, ApiName apiName, CancellationToken cancellationToken)
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

file sealed class IsTagNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(TagName name, ApiName apiName) =>
        DoesTagInformationFileExist(name, apiName);

    private bool DoesTagInformationFileExist(TagName name, ApiName apiName)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutApiTagHandler(FindApiTagDto findDto, PutApi putApi, PutTag putTag, PutApiTagInApim putInApim) : IDisposable
{
    private readonly ApiTagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiName, Put, cancellationToken);

    private async ValueTask Put(TagName name, ApiName apiName, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, apiName, cancellationToken);
        await dtoOption.IterTask(async dto => await Put(name, dto, apiName, cancellationToken));
    }

    private async ValueTask Put(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        await putApi(apiName, cancellationToken);
        await putTag(name, cancellationToken);
        await putInApim(name, dto, apiName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindApiTagDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents)
{
    public async ValueTask<Option<ApiTagDto>> Handle(TagName name, ApiName apiName, CancellationToken cancellationToken)
    {
        var informationFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);
        var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

        return from contents in contentsOption
               select contents.ToObjectFromJson<ApiTagDto>();
    }
}

file sealed class PutApiTagInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Adding tag {TagName} to api {ApiName}...", name, apiName);
        await ApiTagUri.From(name, apiName, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteApiTagHandler(DeleteApiTagFromApim deleteFromApim) : IDisposable
{
    private readonly ApiTagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, ApiName apiName, CancellationToken cancellationToken) =>
        await semaphore.Run(name, apiName, Delete, cancellationToken);

    private async ValueTask Delete(TagName name, ApiName apiName, CancellationToken cancellationToken)
    {
        await deleteFromApim(name, apiName, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteApiTagFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting tag {TagName} from api {ApiName}...", name, apiName);
        await ApiTagUri.From(name, apiName, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class ApiTagServices
{
    public static void ConfigureFindApiTagAction(IServiceCollection services)
    {
        ConfigureTryParseTagName(services);
        ConfigureProcessApiTag(services);

        services.TryAddSingleton<FindApiTagActionHandler>();
        services.TryAddSingleton<FindApiTagAction>(provider => provider.GetRequiredService<FindApiTagActionHandler>().Handle);
    }

    private static void ConfigureTryParseTagName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseTagNameHandler>();
        services.TryAddSingleton<TryParseTagName>(provider => provider.GetRequiredService<TryParseTagNameHandler>().Handle);
    }

    private static void ConfigureProcessApiTag(IServiceCollection services)
    {
        ConfigureIsTagNameInSourceControl(services);
        ConfigurePutApiTag(services);
        ConfigureDeleteApiTag(services);

        services.TryAddSingleton<ProcessApiTagHandler>();
        services.TryAddSingleton<ProcessApiTag>(provider => provider.GetRequiredService<ProcessApiTagHandler>().Handle);
    }

    private static void ConfigureIsTagNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsTagNameInSourceControlHandler>();
        services.TryAddSingleton<IsTagNameInSourceControl>(provider => provider.GetRequiredService<IsTagNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutApiTag(IServiceCollection services)
    {
        ConfigureFindApiTagDto(services);
        ConfigurePutApiTagInApim(services);
        ApiServices.ConfigurePutApi(services);
        TagServices.ConfigurePutTag(services);

        services.TryAddSingleton<PutApiTagHandler>();
        services.TryAddSingleton<PutApiTag>(provider => provider.GetRequiredService<PutApiTagHandler>().Handle);
    }

    private static void ConfigureFindApiTagDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindApiTagDtoHandler>();
        services.TryAddSingleton<FindApiTagDto>(provider => provider.GetRequiredService<FindApiTagDtoHandler>().Handle);
    }

    private static void ConfigurePutApiTagInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutApiTagInApimHandler>();
        services.TryAddSingleton<PutApiTagInApim>(provider => provider.GetRequiredService<PutApiTagInApimHandler>().Handle);
    }

    private static void ConfigureDeleteApiTag(IServiceCollection services)
    {
        ConfigureDeleteApiTagFromApim(services);

        services.TryAddSingleton<DeleteApiTagHandler>();
        services.TryAddSingleton<DeleteApiTag>(provider => provider.GetRequiredService<DeleteApiTagHandler>().Handle);
    }

    private static void ConfigureDeleteApiTagFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteApiTagFromApimHandler>();
        services.TryAddSingleton<DeleteApiTagFromApim>(provider => provider.GetRequiredService<DeleteApiTagFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("ApiTagPublisher");
}