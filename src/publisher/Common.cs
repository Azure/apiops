using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

namespace publisher;

internal delegate FileOperations GetCurrentFileOperations();

internal static class CommonModule
{
    public static void ConfigureGetCurrentFileOperations(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureGetCurrentCommitFileOperations(builder);
        FileSystemModule.ConfigureGetLocalFileOperations(builder);

        builder.TryAddSingleton(ResolveGetCurrentFileOperations);
    }

    internal static GetCurrentFileOperations ResolveGetCurrentFileOperations(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getCurrentCommitFileOperations = provider.GetRequiredService<GetCurrentCommitFileOperations>();
        var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();

        return () => commitIdWasPassed()
                     ? getCurrentCommitFileOperations()
                        .IfNone(() => throw new InvalidOperationException("Could not get file operations for the current commit."))
                     : getLocalFileOperations();
    }
}