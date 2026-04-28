using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;

namespace publisher;

internal delegate FileOperations GetCurrentFileOperations();
internal delegate bool IsDryRun();

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

    public static void ConfigureIsDryRun(IHostApplicationBuilder builder)
    {
        builder.TryAddSingleton(ResolveIsDryRun);
    }

    internal static IsDryRun ResolveIsDryRun(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var logger = provider.GetRequiredService<ILogger>();

        var lazy = new Lazy<bool>(() =>
        {
            var enabled = configuration.GetValue("DRY_RUN")
                                       .Bind(value => bool.TryParse(value, out var result)
                                                        ? Option.Some(result)
                                                        : Option.None)
                                       .IfNone(() => false);

            if (enabled)
            {
                logger.LogWarning("Running in dry-run mode. No changes will be made.");
            }

            return enabled;
        });

        return () => lazy.Value;
    }
}