using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public delegate ValueTask<Option<BinaryData>> ReadFile(FileInfo file, CancellationToken cancellationToken);
public delegate Option<IEnumerable<DirectoryInfo>> GetSubDirectories(DirectoryInfo directory);
public delegate FileOperations GetLocalFileOperations();

public sealed record FileOperations
{
    public required ReadFile ReadFile { get; init; }
    public required GetSubDirectories GetSubDirectories { get; init; }
    public required Func<ImmutableHashSet<FileInfo>> EnumerateServiceDirectoryFiles { get; init; }
}


public static class DirectoryInfoModule
{
    public static DirectoryInfo GetChildDirectory(this DirectoryInfo directory, string path) =>
        new(Path.Combine(directory.FullName, path));

    public static IEnumerable<DirectoryInfo> GetChildDirectories(this DirectoryInfo directory) =>
        directory.Exists()
        ? directory.EnumerateDirectories()
        : [];

    public static FileInfo GetChildFile(this DirectoryInfo directory, string path) =>
        new(Path.Combine(directory.FullName, path));

    public static IEnumerable<FileInfo> GetChildFiles(this DirectoryInfo directory) =>
        directory.Exists()
        ? directory.EnumerateFiles()
        : [];

    /// <summary>
    /// Refreshes the state of the directory, then checks if it exists. There are times when simply checking
    /// <seealso cref="DirectoryInfo.Exists"/> returns false even though the directory exists.
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    public static bool Exists(this DirectoryInfo directory)
    {
        directory.Refresh();

        return directory.Exists;
    }

    public static void DeleteIfExists(this DirectoryInfo directory)
    {
        try
        {
            Retry.Pipeline.Execute(() =>
            {
                if (directory.Exists())
                {
                    directory.EnumerateFiles("*", SearchOption.AllDirectories)
                             .Iter(file =>
                             {
                                 if (file.IsReadOnly)
                                 {
                                     file.IsReadOnly = false;
                                 }
                             });

                    directory.Delete(recursive: true);
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class FileInfoModule
{
    public static EqualityComparer<FileInfo> Comparer { get; } =
        EqualityComparer<FileInfo>.Create(equals: (first, second) => string.Equals(first?.FullName, second?.FullName),
                                          getHashCode: file => file?.FullName?.GetHashCode() ?? 0);

    public static async ValueTask OverwriteWithBinaryData(this FileInfo file, BinaryData data, CancellationToken cancellationToken) =>
        await Retry.Pipeline.ExecuteAsync(async cancellationToken =>
        {
            file.EnsureDirectoryExists();

            await File.WriteAllBytesAsync(file.FullName, data.ToArray(), cancellationToken);
        }, cancellationToken);

    public static async ValueTask OverwriteWithJson(this FileInfo file, JsonNode json, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };

        var binaryData = BinaryData.FromObjectAsJson(json, options);

        await file.OverwriteWithBinaryData(binaryData, cancellationToken);
    }

    private static void EnsureDirectoryExists(this FileInfo file)
    {
        var directory = file.Directory
                        ?? throw new InvalidOperationException($"File {file.FullName} has a null directory.");

        if (directory.Exists() is false)
        {
            directory.Create();
        }
    }

    /// <summary>
    /// Refreshes the state of the file, then checks if it exists. There are times when simply checking
    /// <seealso cref="FileInfo.Exists"/> returns false even though the file exists.
    /// </summary>
    public static bool Exists(this FileInfo file)
    {
        file.Refresh();

        return file.Exists;
    }

    public static async ValueTask<Option<BinaryData>> ReadAsBinaryData(this FileInfo file, CancellationToken cancellationToken) =>
        await Retry.Pipeline.ExecuteAsync(async cancellationToken =>
        {
            if (file.Exists() is false)
            {
                return Option<BinaryData>.None();
            }

            var contents = await File.ReadAllBytesAsync(file.FullName, cancellationToken);

            return BinaryData.FromBytes(contents);
        }, cancellationToken);
}

file static class Retry
{
    public static ResiliencePipeline Pipeline { get; } =
        new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions()
        {
            ShouldHandle = async arguments =>
            {
                await ValueTask.CompletedTask;

                return arguments.Outcome.Exception is IOException or UnauthorizedAccessException;
            }
        })
        .Build();
}

public static class FileSystemModule
{
    public static void ConfigureGetLocalFileOperations(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(ResolveGetLocalFileOperations);
    }

    private static GetLocalFileOperations ResolveGetLocalFileOperations(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();

        return () => new FileOperations
        {
            ReadFile = async (file, cancellationToken) => await file.ReadAsBinaryData(cancellationToken),
            GetSubDirectories = directory => Option.Some(directory.EnumerateDirectories()),
            EnumerateServiceDirectoryFiles = () => serviceDirectory.ToDirectoryInfo()
                                                                    .EnumerateFiles("*", SearchOption.AllDirectories)
                                                                    .ToImmutableHashSet(FileInfoModule.Comparer)
        };
    }
}