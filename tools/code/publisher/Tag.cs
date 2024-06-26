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

internal delegate Option<PublisherAction> FindTagAction(FileInfo file);

file delegate Option<TagName> TryParseTagName(FileInfo file);

file delegate ValueTask ProcessTag(TagName name, CancellationToken cancellationToken);

file delegate bool IsTagNameInSourceControl(TagName name);

file delegate ValueTask<Option<TagDto>> FindTagDto(TagName name, CancellationToken cancellationToken);

internal delegate ValueTask PutTag(TagName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteTag(TagName name, CancellationToken cancellationToken);

file delegate ValueTask PutTagInApim(TagName name, TagDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteTagFromApim(TagName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingTag(TagName name, CancellationToken cancellationToken);

file sealed class FindTagActionHandler(TryParseTagName tryParseName, ProcessTag processTag)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(TagName name) =>
        async cancellationToken => await processTag(name, cancellationToken);
}

file sealed class TryParseTagNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<TagName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<TagName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in TagInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class TagSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<TagName> locker = new(LockOptions.Default);
    private ImmutableHashSet<TagName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(TagName name, Func<TagName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessTagHandler(IsTagNameInSourceControl isNameInSourceControl, PutTag put, DeleteTag delete) : IDisposable
{
    private readonly TagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(TagName name, CancellationToken cancellationToken)
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

file sealed class IsTagNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(TagName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(TagName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = TagInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutTagHandler(FindTagDto findDto, PutTagInApim putInApim) : IDisposable
{
    private readonly TagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(TagName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindTagDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<TagDto>> Handle(TagName name, CancellationToken cancellationToken)
    {
        var informationFile = TagInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<TagDto>()
               let overrideDto = overrideFactory.Create<TagName, TagDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutTagInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, TagDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting tag {TagName}...", name);
        await TagUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteTagHandler(IEnumerable<OnDeletingTag> onDeletingHandlers, DeleteTagFromApim deleteFromApim) : IDisposable
{
    private readonly TagSemaphore semaphore = new();

    public async ValueTask Handle(TagName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(TagName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteTagFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting tag {TagName}...", name);
        await TagUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class TagServices
{
    public static void ConfigureFindTagAction(IServiceCollection services)
    {
        ConfigureTryParseTagName(services);
        ConfigureProcessTag(services);

        services.TryAddSingleton<FindTagActionHandler>();
        services.TryAddSingleton<FindTagAction>(provider => provider.GetRequiredService<FindTagActionHandler>().Handle);
    }

    private static void ConfigureTryParseTagName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseTagNameHandler>();
        services.TryAddSingleton<TryParseTagName>(provider => provider.GetRequiredService<TryParseTagNameHandler>().Handle);
    }

    private static void ConfigureProcessTag(IServiceCollection services)
    {
        ConfigureIsTagNameInSourceControl(services);
        ConfigurePutTag(services);
        ConfigureDeleteTag(services);

        services.TryAddSingleton<ProcessTagHandler>();
        services.TryAddSingleton<ProcessTag>(provider => provider.GetRequiredService<ProcessTagHandler>().Handle);
    }

    private static void ConfigureIsTagNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsTagNameInSourceControlHandler>();
        services.TryAddSingleton<IsTagNameInSourceControl>(provider => provider.GetRequiredService<IsTagNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutTag(IServiceCollection services)
    {
        ConfigureFindTagDto(services);
        ConfigurePutTagInApim(services);

        services.TryAddSingleton<PutTagHandler>();
        services.TryAddSingleton<PutTag>(provider => provider.GetRequiredService<PutTagHandler>().Handle);
    }

    private static void ConfigureFindTagDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindTagDtoHandler>();
        services.TryAddSingleton<FindTagDto>(provider => provider.GetRequiredService<FindTagDtoHandler>().Handle);
    }

    private static void ConfigurePutTagInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutTagInApimHandler>();
        services.TryAddSingleton<PutTagInApim>(provider => provider.GetRequiredService<PutTagInApimHandler>().Handle);
    }

    private static void ConfigureDeleteTag(IServiceCollection services)
    {
        ConfigureOnDeletingTag(services);
        ConfigureDeleteTagFromApim(services);

        services.TryAddSingleton<DeleteTagHandler>();
        services.TryAddSingleton<DeleteTag>(provider => provider.GetRequiredService<DeleteTagHandler>().Handle);
    }

    private static void ConfigureOnDeletingTag(IServiceCollection services)
    {
    }

    private static void ConfigureDeleteTagFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteTagFromApimHandler>();
        services.TryAddSingleton<DeleteTagFromApim>(provider => provider.GetRequiredService<DeleteTagFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("TagPublisher");
}