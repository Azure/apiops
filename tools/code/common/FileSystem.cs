using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public static class DirectoryInfoExtensions
{
    public static DirectoryInfo GetChildDirectory(this DirectoryInfo directory, string path) =>
        new(Path.Combine(directory.FullName, path));

    public static FileInfo GetChildFile(this DirectoryInfo directory, string path) =>
        new(Path.Combine(directory.FullName, path));

    public static Option<DirectoryInfo> TryGetParentDirectory(this DirectoryInfo directory) =>
        directory.Parent is DirectoryInfo parent
            ? parent
            : Option<DirectoryInfo>.None;

    public static IEnumerable<DirectoryInfo> ListDirectories(this DirectoryInfo directory, string directoryPattern) =>
        directory.Exists()
        ? directory.EnumerateDirectories(directoryPattern)
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

    public static IEnumerable<FileInfo> ListFiles(this DirectoryInfo directory, string filePattern) =>
        directory.Exists()
        ? directory.EnumerateFiles(filePattern)
        : [];

    public static void ForceDelete(this DirectoryInfo directory)
    {
        if (directory.Exists() is false)
        {
            return;
        };

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
}

public static class FileInfoExtensions
{
    public static async ValueTask OverwriteWithBinaryData(this FileInfo file, BinaryData data, CancellationToken cancellationToken)
    {
        file.EnsureDirectoryExists();

        await File.WriteAllBytesAsync(file.FullName, data.ToArray(), cancellationToken);
    }

    public static async ValueTask OverwriteWithJson(this FileInfo file, JsonNode json, CancellationToken cancellationToken)
    {
        var binaryData = BinaryData.FromObjectAsJson(json, JsonObjectExtensions.SerializerOptions);

        await file.OverwriteWithBinaryData(binaryData, cancellationToken);
    }

    private static void EnsureDirectoryExists(this FileInfo file)
    {
        var directory = file.Directory
                        ?? throw new InvalidOperationException($"File {file.FullName} has a null directory.");

        if (!directory.Exists)
        {
            directory.Create();
        }
    }

    public static async ValueTask<T> ReadAsJson<T>(this FileInfo file, CancellationToken cancellationToken)
    {
        var binaryData = await file.ReadAsBinaryData(cancellationToken);
        return binaryData.ToObjectFromJson<T>();
    }

    public static async ValueTask<BinaryData> ReadAsBinaryData(this FileInfo file, CancellationToken cancellationToken)
    {
        using var stream = file.OpenRead();
        return await BinaryData.FromStreamAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Refreshes the state of the file, then checks if it exists. There are times when simply checking
    /// <seealso cref="FileInfo.Exists"/> returns false even though the file exists.
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static bool Exists(this FileInfo file)
    {
        file.Refresh();

        return file.Exists;
    }
}