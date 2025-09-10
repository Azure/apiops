using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<BinaryData>> ReadCurrentFile(FileInfo file, CancellationToken cancellationToken);
internal delegate ImmutableHashSet<FileInfo> ListCurrentServiceDirectoryFiles();

internal static class CommonModule
{
    public static void ConfigureReadCurrentFile(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureReadCurrentCommitFile(builder);
        FileSystemModule.ConfigureReadLocalFile(builder);

        builder.TryAddSingleton(GetReadCurrentFile);
    }

    private static ReadCurrentFile GetReadCurrentFile(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var readCurrentCommitFile = provider.GetRequiredService<ReadCurrentCommitFile>();
        var readLocalFile = provider.GetRequiredService<ReadLocalFile>();

        return async (file, cancellationToken) =>
            commitIdWasPassed()
                ? await readCurrentCommitFile(file, cancellationToken)
                : await readLocalFile(file, cancellationToken);
    }

    public static void ConfigureListCurrentServiceDirectoryFiles(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureListCurrentCommitServiceDirectoryFiles(builder);
        FileSystemModule.ConfigureListLocalServiceDirectoryFiles(builder);

        builder.TryAddSingleton(GetListCurrentServiceDirectoryFiles);
    }

    private static ListCurrentServiceDirectoryFiles GetListCurrentServiceDirectoryFiles(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var listCurrentCommitServiceDirectoryFiles = provider.GetRequiredService<ListCurrentCommitServiceDirectoryFiles>();
        var listLocalServiceDirectoryFiles = provider.GetRequiredService<ListLocalServiceDirectoryFiles>();

        return () =>
            commitIdWasPassed()
                ? listCurrentCommitServiceDirectoryFiles()
                    .IfNone(() => throw new InvalidOperationException("Failed to list service directory files in the current commit."))
                : listLocalServiceDirectoryFiles();
    }

    public static void ConfigureGetSubDirectories(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureGetCurrentCommitSubDirectories(builder);

        builder.TryAddSingleton<GetSubDirectories>(GetGetSubDirectories);
    }

    private static GetSubDirectories GetGetSubDirectories(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getCurrentCommitSubDirectories = provider.GetRequiredService<GetCurrentCommitSubDirectories>();

        return directory => commitIdWasPassed()
            ? getCurrentCommitSubDirectories(directory)
            : Option.Some(directory.GetChildDirectories());
    }
}