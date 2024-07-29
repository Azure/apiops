using Azure.Core;
using common;
using LanguageExt;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Frozen;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

/// <summary>
/// Get files that changed in the commit. If no commit ID is provided, it will return all artifact files.
/// </summary>
/// <returns></returns>
public delegate FrozenSet<FileInfo> GetPublisherFiles();

/// <summary>
/// Lists all files in the artifact directory. If a commit ID is provided, it will list
/// all files as of that commit.
/// </summary>
public delegate FrozenSet<FileInfo> GetArtifactFiles();

/// <summary>
/// Returns a dictionary of files in the previous commit. The key is the file,
/// and the value is a function that returns its contents. If the dictionary contained the actual
/// contents, it could get prohibitively large.
/// </summary>
/// <returns></returns>
public delegate FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>> GetArtifactsInPreviousCommit();

/// <summary>
/// Gets the contents of a file. If the publisher is running in the context of a Git commit,
/// the file contents will be retrieved from the Git repository as of that commit ID.
/// Otherwise, the file contents will come from the local file system.
/// If the file does not exist, returns <see cref="Option"/>.None.
/// </summary>
public delegate ValueTask<Option<BinaryData>> TryGetFileContents(FileInfo fileInfo, CancellationToken cancellationToken);

/// <summary>
/// An action to be performed by the publisher. Shortcut for a function
/// that takes a <see cref="CancellationToken"/> and returns a <see cref="ValueTask"/>.
/// </summary>
public delegate ValueTask PublisherAction(CancellationToken cancellationToken);

public delegate Option<CommitId> TryGetCommitId();

public delegate ValueTask<Option<BinaryData>> TryGetFileContentsInCommit(FileInfo fileInfo, CommitId commitId, CancellationToken cancellationToken);

internal static class CommonModule
{
    public static void ConfigureGetPublisherFiles(IHostApplicationBuilder builder)
    {
        ConfigureTryGetCommitId(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);
        builder.Services.AddMemoryCache();

        builder.Services.TryAddSingleton(GetGetPublisherFiles);
    }

    private static GetPublisherFiles GetGetPublisherFiles(IServiceProvider provider)
    {
        var tryGetCommitId = provider.GetRequiredService<TryGetCommitId>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        var cacheKey = Guid.NewGuid().ToString();

        return () =>
            cache.GetOrCreate(cacheKey, _ => getFiles())!;

        FrozenSet<FileInfo> getFiles() =>
            tryGetCommitId()
                .Map(getFilesFromCommitId)
                .IfNone(serviceDirectory.GetFilesRecursively);

        FrozenSet<FileInfo> getFilesFromCommitId(CommitId commitId) =>
            Git.GetChangedFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId);
    }

    public static void ConfigureGetArtifactFiles(IHostApplicationBuilder builder)
    {
        ConfigureTryGetCommitId(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);
        builder.Services.AddMemoryCache();

        builder.Services.TryAddSingleton(GetGetArtifactFiles);
    }

    private static GetArtifactFiles GetGetArtifactFiles(IServiceProvider provider)
    {
        var tryGetCommitId = provider.GetRequiredService<TryGetCommitId>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        var cacheKey = Guid.NewGuid().ToString();

        return () =>
            cache.GetOrCreate(cacheKey, _ => getFiles())!;

        FrozenSet<FileInfo> getFiles() =>
            tryGetCommitId()
                .Map(getFilesFromCommitId)
                .IfNone(serviceDirectory.GetFilesRecursively);

        FrozenSet<FileInfo> getFilesFromCommitId(CommitId commitId) =>
            Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), commitId);
    }

    public static void ConfigureGetArtifactsInPreviousCommit(IHostApplicationBuilder builder)
    {
        ConfigureTryGetCommitId(builder);
        ConfigureTryGetFileContentsInCommit(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);
        builder.Services.AddMemoryCache();

        builder.Services.TryAddSingleton(GetGetArtifactsInPreviousCommit);
    }

    private static GetArtifactsInPreviousCommit GetGetArtifactsInPreviousCommit(IServiceProvider provider)
    {
        var tryGetCommitId = provider.GetRequiredService<TryGetCommitId>();
        var tryGetCommitContents = provider.GetRequiredService<TryGetFileContentsInCommit>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var cache = provider.GetRequiredService<IMemoryCache>();

        var cacheKey = Guid.NewGuid().ToString();

        return () =>
            cache.GetOrCreate(cacheKey, _ => getArtifacts())!;

        FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>> getArtifacts() =>
            tryGetCommitId()
                .Bind(tryGetPreviousCommitArtifacts)
                .IfNone(() => FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>>.Empty);

        Option<FrozenDictionary<FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>>> tryGetPreviousCommitArtifacts(CommitId commitId) =>
            Git.TryGetPreviousCommitId(serviceDirectory.ToDirectoryInfo(), commitId)
               .Map(previousCommitId => Git.GetExistingFilesInCommit(serviceDirectory.ToDirectoryInfo(), previousCommitId)
                                           .ToFrozenDictionary<FileInfo, FileInfo, Func<CancellationToken, ValueTask<Option<BinaryData>>>>
                                           (keySelector: file => file,
                                            elementSelector: file => (CancellationToken cancellationToken) => tryGetCommitContents(file, previousCommitId, cancellationToken)));
    }

    public static void ConfigureTryGetFileContents(IHostApplicationBuilder builder)
    {
        ConfigureTryGetCommitId(builder);
        ConfigureTryGetFileContentsInCommit(builder);

        builder.Services.TryAddSingleton(GetTryGetFileContents);
    }

    private static TryGetFileContents GetTryGetFileContents(IServiceProvider provider)
    {
        var tryGetCommitId = provider.GetRequiredService<TryGetCommitId>();
        var tryGetCommitContents = provider.GetRequiredService<TryGetFileContentsInCommit>();

        return async (file, cancellationToken) =>
            await tryGetCommitId()
                    .BindTask(async commitId => await tryGetCommitContents(file, commitId, cancellationToken))
                    .Or(async () => await tryGetFileSystemContents(file, cancellationToken));

        static async ValueTask<Option<BinaryData>> tryGetFileSystemContents(FileInfo file, CancellationToken cancellationToken) =>
            file.Exists()
            ? await file.ReadAsBinaryData(cancellationToken)
            : Option<BinaryData>.None;
    }

    private static void ConfigureTryGetFileContentsInCommit(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryGetFileContentsInCommit);
    }

    private static TryGetFileContentsInCommit GetTryGetFileContentsInCommit(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return async (file, commitId, cancellationToken) =>
            await Git.TryGetFileContentsInCommit(serviceDirectory.ToDirectoryInfo(), file, commitId)
                     .MapTask(async stream =>
                     {
                         using (stream)
                         {
                             return await BinaryData.FromStreamAsync(stream, cancellationToken);
                         }
                     });
    }

    private static void ConfigureTryGetCommitId(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetTryGetCommitId);
    }

    private static TryGetCommitId GetTryGetCommitId(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return () =>
            configuration.TryGetValue("COMMIT_ID")
                         .Map(commitId => new CommitId(commitId));
    }
}

file static class Common
{
    public static FrozenSet<FileInfo> GetFilesRecursively(this ManagementServiceDirectory serviceDirectory) =>
        serviceDirectory.ToDirectoryInfo()
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .ToFrozenSet(x => x.FullName);
}