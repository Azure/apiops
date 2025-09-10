using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<BinaryData>> ReadLocalFile(FileInfo file, CancellationToken cancellationToken);
internal delegate ImmutableHashSet<FileInfo> ListLocalServiceDirectoryFiles();

internal static class FileSystemModule
{
    public static void ConfigureReadLocalFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetReadLocalFile);
    }

    private static ReadLocalFile GetReadLocalFile(IServiceProvider provider) =>
        async (file, cancellationToken) => await file.ReadAsBinaryData(cancellationToken);

    public static void ConfigureListLocalServiceDirectoryFiles(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetListLocalServiceDirectoryFiles);
    }

    private static ListLocalServiceDirectoryFiles GetListLocalServiceDirectoryFiles(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        var lazy = new Lazy<ImmutableHashSet<FileInfo>>(() =>
            serviceDirectory.ToDirectoryInfo()
                            .EnumerateFiles("*", SearchOption.AllDirectories)
                            .ToImmutableHashSet(FileInfoModule.Comparer));

        return () => lazy.Value;
    }
}
