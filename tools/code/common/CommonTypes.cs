using Flurl;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ArtifactPath
{
    private readonly string value;

    public ArtifactPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Record path cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public static class ArtifactPathExtensions
{
    public static ArtifactPath Append(this ArtifactPath artifactPath, string pathToAppend)
    {
        string newPath = Path.Combine(artifactPath.ToString(), pathToAppend);
        return new ArtifactPath(newPath);
    }
}

public interface IArtifactFile
{
    ArtifactPath Path { get; }
}

public static class ArtifactFileExtensions
{
    public static string GetNameWithoutExtensions(this IArtifactFile file)
    {
        return Path.GetFileNameWithoutExtension(file.Path.ToString());
    }

    internal static string GetName(this IArtifactFile file)
    {
        return file.GetFileInfo().Name;
    }

    private static FileInfo GetFileInfo(this IArtifactFile file)
    {
        return new FileInfo(file.Path.ToString());
    }

    public static bool Exists(this IArtifactFile file)
    {
        return file.GetFileInfo().Exists;
    }

    public static async ValueTask<string> ReadAsString(this IArtifactFile file, CancellationToken cancellationToken)
    {
        return await File.ReadAllTextAsync(file.Path.ToString(), cancellationToken);
    }

    public static JsonObject ReadAsJsonObject(this IArtifactFile file)
    {
        return file.ReadAsJsonNode()
                   .AsObject();
    }

    public static JsonArray ReadAsJsonArray(this IArtifactFile file)
    {
        return file.ReadAsJsonNode()
                   .AsArray();
    }

    private static JsonNode ReadAsJsonNode(this IArtifactFile file)
    {
        using var stream = file.ReadAsStream();
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };

        return JsonNode.Parse(stream, options)
                ?? throw new InvalidOperationException($"Could not read JSON from file {file.Path}.");
    }

    public static Stream ReadAsStream(this IArtifactFile file)
    {
        return file.GetFileInfo().OpenRead();
    }

    public static async ValueTask OverwriteWithJson(this IArtifactFile file, JsonNode json, CancellationToken cancellationToken)
    {
        file.CreateDirectoryIfNotExists();

        using var stream = file.GetFileInfo().Open(FileMode.Create);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await JsonSerializer.SerializeAsync(stream, json, options, cancellationToken);
    }

    private static void CreateDirectoryIfNotExists(this IArtifactFile file)
    {
        var directory = file.GetFileInfo().Directory
            ?? throw new InvalidOperationException($"File {file.Path} has a null directory.");

        if (directory.Exists is false)
        {
            directory.Create();
        }
    }

    public static async ValueTask OverwriteWithText(this IArtifactFile file, string text, CancellationToken cancellationToken)
    {
        file.CreateDirectoryIfNotExists();
        await File.WriteAllTextAsync(file.Path.ToString(), text, cancellationToken);
    }

    public static async ValueTask OverwriteWithBytes(this IArtifactFile file, byte[] bytes, CancellationToken cancellationToken)
    {
        file.CreateDirectoryIfNotExists();
        await File.WriteAllBytesAsync(file.Path.ToString(), bytes, cancellationToken);
    }

    public static async ValueTask OverwriteWithStream(this IArtifactFile file, Stream stream, CancellationToken cancellationToken)
    {
        file.CreateDirectoryIfNotExists();

        using var fileStream = file.GetFileInfo().Open(FileMode.Create);
        await stream.CopyToAsync(fileStream, cancellationToken);
    }
}

public interface IArtifactDirectory
{
    ArtifactPath Path { get; }
}

public static class ArtifactDirectoryExtensions
{
    public static bool PathEquals(this IArtifactDirectory directory, [NotNullWhen(true)] DirectoryInfo? directoryInfo)
    {
        return directory.Path.ToString().Equals(directoryInfo?.FullName);
    }

    public static DirectoryInfo GetDirectoryInfo(this IArtifactDirectory directory)
    {
        return new DirectoryInfo(directory.Path.ToString());
    }

    public static string GetName(this IArtifactDirectory directory)
    {
        return directory.GetDirectoryInfo().Name;
    }

    public static bool DirectoryExists(this IArtifactDirectory directory)
    {
        return directory.GetDirectoryInfo().Exists;
    }

    public static IEnumerable<FileInfo> EnumerateFilesRecursively(this IArtifactDirectory directory)
    {
        return directory.GetDirectoryInfo()
                        .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true });
    }
}

public interface IArtifactUri
{
    public Uri Uri { get; }
}

public static class ArtifactUriExtensions
{
    public static Uri AppendPath(this IArtifactUri artifactUri, string path) =>
        artifactUri.Uri.AppendPathSegment(path)
                       .ToUri();
}