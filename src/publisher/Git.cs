using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate Option<CommitId> GetCurrentCommitId();
internal delegate Option<CommitId> GetPreviousCommitId();
internal delegate bool CommitIdWasPassed();
internal delegate ValueTask<Option<BinaryData>> ReadCurrentCommitFile(FileInfo file, CancellationToken cancellationToken);
internal delegate ValueTask<Option<BinaryData>> ReadPreviousCommitFile(FileInfo file, CancellationToken cancellationToken);
internal delegate Option<ImmutableHashSet<FileInfo>> ListCurrentCommitServiceDirectoryFiles();
internal delegate Option<ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>>> ListServiceDirectoryFilesModifiedByCurrentCommit();
internal delegate Option<IEnumerable<DirectoryInfo>> GetCurrentCommitSubDirectories(DirectoryInfo directory);
internal delegate Option<IEnumerable<DirectoryInfo>> GetPreviousCommitSubDirectories(DirectoryInfo directory);

internal static class GitModule
{
    public static void ConfigureGetCurrentCommitId(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(GetGetCurrentCommitId);

    private static GetCurrentCommitId GetGetCurrentCommitId(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var logger = provider.GetRequiredService<ILogger>();

        // This will be called frequently and shouldn't change while the code is running,
        // so we use a Lazy to cache the result. We also want to run the logging operation once.
        var lazy = new Lazy<Option<CommitId>>(() =>
        {
            var commitIdOption = configuration.GetValue("COMMIT_ID")
                                              .Map(commitId => CommitId.From(commitId)
                                                                       .IfErrorThrow());

            commitIdOption.Match(commitId => logger.LogInformation("Using commit ID: {CommitId}", commitId),
                                 () => logger.LogInformation("No commit ID provided."));

            return commitIdOption;
        });

        return () => lazy.Value;
    }

    public static void ConfigureGetPreviousCommitId(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetGetPreviousCommitId);
    }

    private static GetPreviousCommitId GetGetPreviousCommitId(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCurrentCommitId()
                .Map(commitId => common.GitModule
                                       .GetPreviousCommitId(commitId, serviceDirectory.ToDirectoryInfo())
                                       .IfNone(() => throw new InvalidOperationException($"No previous commit ID found for commit '{commitId}' in {serviceDirectory}.")));
    }

    public static void ConfigureCommitIdWasPassed(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);

        builder.TryAddSingleton(GetCommitIdWasPassed);
    }

    private static CommitIdWasPassed GetCommitIdWasPassed(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();

        return () => getCurrentCommitId().IsSome;
    }

    public static void ConfigureReadCurrentCommitFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetReadCurrentCommitFile);
    }

    private static ReadCurrentCommitFile GetReadCurrentCommitFile(IServiceProvider provider)
    {
        var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();

        return async (file, cancellationToken) =>
            await getCommitId()
                    .BindTask(commitId => common.GitModule
                                                .ReadFile(file, commitId, cancellationToken));
    }

    public static void ConfigureReadPreviousCommitFile(IHostApplicationBuilder builder)
    {
        ConfigureGetPreviousCommitId(builder);

        builder.TryAddSingleton(GetReadPreviousCommitFile);
    }

    private static ReadPreviousCommitFile GetReadPreviousCommitFile(IServiceProvider provider)
    {
        var getCommitId = provider.GetRequiredService<GetPreviousCommitId>();

        return async (file, cancellationToken) =>
            await getCommitId()
                    .BindTask(commitId => common.GitModule
                                                .ReadFile(file, commitId, cancellationToken));
    }

    public static void ConfigureListCurrentCommitServiceDirectoryFiles(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetListCurrentCommitServiceDirectoryFiles);
    }

    private static ListCurrentCommitServiceDirectoryFiles GetListCurrentCommitServiceDirectoryFiles(IServiceProvider provider)
    {
        var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        var repositoryDirectory = serviceDirectory.ToDirectoryInfo();

        return () =>
            getCommitId()
                .Map(commitId => common.GitModule.GetCommitFiles(commitId, repositoryDirectory));
    }

    public static void ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetListServiceDirectoryFilesModifiedByCurrentCommit);
    }

    private static ListServiceDirectoryFilesModifiedByCurrentCommit GetListServiceDirectoryFilesModifiedByCurrentCommit(IServiceProvider provider)
    {
        var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCommitId()
                .Map(commitId => common.GitModule.GetFilesModifiedByCommit(commitId, serviceDirectory.ToDirectoryInfo()));
    }

    public static void ConfigureGetCurrentCommitSubDirectories(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);

        builder.TryAddSingleton(GetGetCurrentCommitSubDirectories);
    }

    private static GetCurrentCommitSubDirectories GetGetCurrentCommitSubDirectories(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();

        return directory =>
            getCurrentCommitId()
                .Bind(commitId => common.GitModule.GetSubDirectories(commitId, directory));
    }

    public static void ConfigureGetPreviousCommitSubDirectories(IHostApplicationBuilder builder)
    {
        ConfigureGetPreviousCommitId(builder);

        builder.TryAddSingleton(GetGetPreviousCommitSubDirectories);
    }

    private static GetPreviousCommitSubDirectories GetGetPreviousCommitSubDirectories(IServiceProvider provider)
    {
        var getPreviousCommitId = provider.GetRequiredService<GetPreviousCommitId>();

        return directory =>
            getPreviousCommitId()
                .Bind(commitId => common.GitModule.GetSubDirectories(commitId, directory));
    }
}
