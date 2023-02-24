using System;
using System.IO;
using System.Linq;
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

    public static async ValueTask<string> ReadAsStringWithoutWhitespace(this FileInfo file, CancellationToken cancellationToken)
    {
        var stringValue = await file.ReadAsString(cancellationToken);
        var characters = stringValue.Where(character => char.IsWhiteSpace(character) is false);
        return string.Concat(characters);
    }

    public static async ValueTask<string> ReadAsString(this FileInfo file, CancellationToken cancellationToken)
    {
        return await File.ReadAllTextAsync(file.FullName, cancellationToken);
    }
}
