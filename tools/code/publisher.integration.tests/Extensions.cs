using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

internal static class FileInfoExtensions
{
    public static string GetNameWithoutExtension(this FileInfo file)
    {
        return Path.GetFileNameWithoutExtension(file.FullName);
    }

    public static DirectoryInfo GetNonNullableDirectory(this FileInfo file)
    {
        return file.Directory ?? throw new InvalidOperationException($"File '{file.Name}' has a null directory.");
    }

    public static async ValueTask<string> ReadAsString(this FileInfo file, CancellationToken cancellationToken)
    {
        return await File.ReadAllTextAsync(file.FullName, cancellationToken);
    }
}
