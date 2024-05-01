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

internal delegate Option<PublisherAction> FindGroupAction(FileInfo file);

file delegate Option<GroupName> TryParseGroupName(FileInfo file);

file delegate ValueTask ProcessGroup(GroupName name, CancellationToken cancellationToken);

file delegate bool IsGroupNameInSourceControl(GroupName name);

file delegate ValueTask<Option<GroupDto>> FindGroupDto(GroupName name, CancellationToken cancellationToken);

internal delegate ValueTask PutGroup(GroupName name, CancellationToken cancellationToken);

file delegate ValueTask DeleteGroup(GroupName name, CancellationToken cancellationToken);

file delegate ValueTask PutGroupInApim(GroupName name, GroupDto dto, CancellationToken cancellationToken);

file delegate ValueTask DeleteGroupFromApim(GroupName name, CancellationToken cancellationToken);

internal delegate ValueTask OnDeletingGroup(GroupName name, CancellationToken cancellationToken);

file sealed class FindGroupActionHandler(TryParseGroupName tryParseName, ProcessGroup processGroup)
{
    public Option<PublisherAction> Handle(FileInfo file) =>
        from name in tryParseName(file)
        select GetAction(name);

    private PublisherAction GetAction(GroupName name) =>
        async cancellationToken => await processGroup(name, cancellationToken);
}

file sealed class TryParseGroupNameHandler(ManagementServiceDirectory serviceDirectory)
{
    public Option<GroupName> Handle(FileInfo file) =>
        TryParseNameFromInformationFile(file);

    private Option<GroupName> TryParseNameFromInformationFile(FileInfo file) =>
        from informationFile in GroupInformationFile.TryParse(file, serviceDirectory)
        select informationFile.Parent.Name;
}

/// <summary>
/// Limits the number of simultaneous operations.
/// </summary>
file sealed class GroupSemaphore : IDisposable
{
    private readonly AsyncKeyedLocker<GroupName> locker = new(LockOptions.Default);
    private ImmutableHashSet<GroupName> processedNames = [];

    /// <summary>
    /// Runs the provided action, ensuring that each name is processed only once.
    /// </summary>
    public async ValueTask Run(GroupName name, Func<GroupName, CancellationToken, ValueTask> action, CancellationToken cancellationToken)
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

file sealed class ProcessGroupHandler(IsGroupNameInSourceControl isNameInSourceControl, PutGroup put, DeleteGroup delete) : IDisposable
{
    private readonly GroupSemaphore semaphore = new();

    public async ValueTask Handle(GroupName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, HandleInner, cancellationToken);

    private async ValueTask HandleInner(GroupName name, CancellationToken cancellationToken)
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

file sealed class IsGroupNameInSourceControlHandler(GetArtifactFiles getArtifactFiles, ManagementServiceDirectory serviceDirectory)
{
    public bool Handle(GroupName name) =>
        DoesInformationFileExist(name);

    private bool DoesInformationFileExist(GroupName name)
    {
        var artifactFiles = getArtifactFiles();
        var informationFile = GroupInformationFile.From(name, serviceDirectory);

        return artifactFiles.Contains(informationFile.ToFileInfo());
    }
}

file sealed class PutGroupHandler(FindGroupDto findDto, PutGroupInApim putInApim) : IDisposable
{
    private readonly GroupSemaphore semaphore = new();

    public async ValueTask Handle(GroupName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Put, cancellationToken);

    private async ValueTask Put(GroupName name, CancellationToken cancellationToken)
    {
        var dtoOption = await findDto(name, cancellationToken);
        await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class FindGroupDtoHandler(ManagementServiceDirectory serviceDirectory, TryGetFileContents tryGetFileContents, OverrideDtoFactory overrideFactory)
{
    public async ValueTask<Option<GroupDto>> Handle(GroupName name, CancellationToken cancellationToken)
    {
        var informationFile = GroupInformationFile.From(name, serviceDirectory);
        var informationFileInfo = informationFile.ToFileInfo();

        var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

        return from contents in contentsOption
               let dto = contents.ToObjectFromJson<GroupDto>()
               let overrideDto = overrideFactory.Create<GroupName, GroupDto>()
               select overrideDto(name, dto);
    }
}

file sealed class PutGroupInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GroupName name, GroupDto dto, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting group {GroupName}...", name);
        await GroupUri.From(name, serviceUri).PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteGroupHandler(IEnumerable<OnDeletingGroup> onDeletingHandlers, DeleteGroupFromApim deleteFromApim) : IDisposable
{
    private readonly GroupSemaphore semaphore = new();

    public async ValueTask Handle(GroupName name, CancellationToken cancellationToken) =>
        await semaphore.Run(name, Delete, cancellationToken);

    private async ValueTask Delete(GroupName name, CancellationToken cancellationToken)
    {
        await onDeletingHandlers.IterParallel(async handler => await handler(name, cancellationToken), cancellationToken);
        await deleteFromApim(name, cancellationToken);
    }

    public void Dispose() => semaphore.Dispose();
}

file sealed class DeleteGroupFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GroupName name, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting group {GroupName}...", name);
        await GroupUri.From(name, serviceUri).Delete(pipeline, cancellationToken);
    }
}

internal static class GroupServices
{
    public static void ConfigureFindGroupAction(IServiceCollection services)
    {
        ConfigureTryParseGroupName(services);
        ConfigureProcessGroup(services);

        services.TryAddSingleton<FindGroupActionHandler>();
        services.TryAddSingleton<FindGroupAction>(provider => provider.GetRequiredService<FindGroupActionHandler>().Handle);
    }

    private static void ConfigureTryParseGroupName(IServiceCollection services)
    {
        services.TryAddSingleton<TryParseGroupNameHandler>();
        services.TryAddSingleton<TryParseGroupName>(provider => provider.GetRequiredService<TryParseGroupNameHandler>().Handle);
    }

    private static void ConfigureProcessGroup(IServiceCollection services)
    {
        ConfigureIsGroupNameInSourceControl(services);
        ConfigurePutGroup(services);
        ConfigureDeleteGroup(services);

        services.TryAddSingleton<ProcessGroupHandler>();
        services.TryAddSingleton<ProcessGroup>(provider => provider.GetRequiredService<ProcessGroupHandler>().Handle);
    }

    private static void ConfigureIsGroupNameInSourceControl(IServiceCollection services)
    {
        services.TryAddSingleton<IsGroupNameInSourceControlHandler>();
        services.TryAddSingleton<IsGroupNameInSourceControl>(provider => provider.GetRequiredService<IsGroupNameInSourceControlHandler>().Handle);
    }

    public static void ConfigurePutGroup(IServiceCollection services)
    {
        ConfigureFindGroupDto(services);
        ConfigurePutGroupInApim(services);

        services.TryAddSingleton<PutGroupHandler>();
        services.TryAddSingleton<PutGroup>(provider => provider.GetRequiredService<PutGroupHandler>().Handle);
    }

    private static void ConfigureFindGroupDto(IServiceCollection services)
    {
        services.TryAddSingleton<FindGroupDtoHandler>();
        services.TryAddSingleton<FindGroupDto>(provider => provider.GetRequiredService<FindGroupDtoHandler>().Handle);
    }

    private static void ConfigurePutGroupInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutGroupInApimHandler>();
        services.TryAddSingleton<PutGroupInApim>(provider => provider.GetRequiredService<PutGroupInApimHandler>().Handle);
    }

    private static void ConfigureDeleteGroup(IServiceCollection services)
    {
        ConfigureOnDeletingGroup(services);
        ConfigureDeleteGroupFromApim(services);

        services.TryAddSingleton<DeleteGroupHandler>();
        services.TryAddSingleton<DeleteGroup>(provider => provider.GetRequiredService<DeleteGroupHandler>().Handle);
    }

    private static void ConfigureOnDeletingGroup(IServiceCollection services)
    {
    }

    private static void ConfigureDeleteGroupFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteGroupFromApimHandler>();
        services.TryAddSingleton<DeleteGroupFromApim>(provider => provider.GetRequiredService<DeleteGroupFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory factory) =>
        factory.CreateLogger("GroupPublisher");
}