using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.IO;

namespace publisher;

internal delegate Option<FileOperations> GetCurrentCommitFileOperations();
internal delegate Option<FileOperations> GetPreviousCommitFileOperations();
internal delegate Option<CommitId> GetCurrentCommitId();
internal delegate Option<CommitId> GetPreviousCommitId();
internal delegate bool CommitIdWasPassed();
internal delegate Option<ImmutableDictionary<GitAction, ImmutableHashSet<FileInfo>>> ListServiceDirectoryFilesModifiedByCurrentCommit();

internal static class GitModule
{
    public static void ConfigureGetCurrentCommitFileOperations(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetCurrentCommitFileOperations);
    }

    internal static GetCurrentCommitFileOperations ResolveGetCurrentCommitFileOperations(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCurrentCommitId()
                .Map(commitId => new FileOperations
                {
                    ReadFile = async (file, cancellationToken) => await common.GitModule.ReadFile(file, commitId, cancellationToken),
                    GetSubDirectories = directory => common.GitModule.GetSubDirectories(commitId, directory),
                    EnumerateServiceDirectoryFiles = () => common.GitModule.GetCommitFiles(commitId, serviceDirectory.ToDirectoryInfo())
                });
    }

    private static void ConfigureGetCurrentCommitId(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveGetCurrentCommitId);

    internal static GetCurrentCommitId ResolveGetCurrentCommitId(IServiceProvider provider)
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

    public static void ConfigureGetPreviousCommitFileOperations(IHostApplicationBuilder builder)
    {
        ConfigureGetPreviousCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetPreviousCommitFileOperations);
    }

    internal static GetPreviousCommitFileOperations ResolveGetPreviousCommitFileOperations(IServiceProvider provider)
    {
        var getPreviousCommitId = provider.GetRequiredService<GetPreviousCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getPreviousCommitId()
                .Map(commitId => new FileOperations
                {
                    ReadFile = async (file, cancellationToken) => await common.GitModule.ReadFile(file, commitId, cancellationToken),
                    GetSubDirectories = directory => common.GitModule.GetSubDirectories(commitId, directory),
                    EnumerateServiceDirectoryFiles = () => common.GitModule.GetCommitFiles(commitId, serviceDirectory.ToDirectoryInfo())
                });
    }

    private static void ConfigureGetPreviousCommitId(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetPreviousCommitId);
    }

    internal static GetPreviousCommitId ResolveGetPreviousCommitId(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCurrentCommitId()
                .Bind(commitId => common.GitModule.GetPreviousCommitId(commitId, serviceDirectory.ToDirectoryInfo()));
    }

    public static void ConfigureCommitIdWasPassed(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);

        builder.TryAddSingleton(ResolveCommitIdWasPassed);
    }

    internal static CommitIdWasPassed ResolveCommitIdWasPassed(IServiceProvider provider)
    {
        var getCurrentCommitId = provider.GetRequiredService<GetCurrentCommitId>();

        return () => getCurrentCommitId().IsSome;
    }

    public static void ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(IHostApplicationBuilder builder)
    {
        ConfigureGetCurrentCommitId(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveListServiceDirectoryFilesModifiedByCurrentCommit);
    }

    internal static ListServiceDirectoryFilesModifiedByCurrentCommit ResolveListServiceDirectoryFilesModifiedByCurrentCommit(IServiceProvider provider)
    {
        var getCommitId = provider.GetRequiredService<GetCurrentCommitId>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () =>
            getCommitId()
                .Map(commitId => common.GitModule.GetFilesModifiedByCommit(commitId, serviceDirectory.ToDirectoryInfo()));
    }
}
