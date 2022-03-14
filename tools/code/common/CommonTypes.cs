using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public abstract record NonEmptyString
{
    private readonly string value;

    protected NonEmptyString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{nameof(value)}' cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public sealed override string ToString() => value;

    public static implicit operator string(NonEmptyString nonEmptyString) => nonEmptyString.ToString();
}

public abstract record UriRecord
{
    private readonly string value;

    protected UriRecord(Uri value)
    {
        this.value = (value.ToString());
    }

    public override string ToString() => value;

    public Uri ToUri() => new(value);

    public static implicit operator Uri(UriRecord record) => record.ToUri();
}

public sealed record RecordPath : NonEmptyString
{
    public RecordPath(string value) : base(value)
    {
    }

    public RecordPath Append(string path) => new(Path.Combine(this, path));

    public bool PathEquals([NotNullWhen(true)] string? path) => string.Equals(this, path);

    public static RecordPath From(string value) => new(value);
}

public abstract record FileRecord
{
    public RecordPath Path { get; }

    private FileInfo FileInfo => new(Path);

    protected FileRecord(RecordPath path)
    {
        Path = path;
    }

    public bool Exists() => FileInfo.Exists;

    public Stream ReadAsStream() => FileInfo.OpenRead();

    public JsonObject ReadAsJsonObject() => ReadAsJsonNode().AsObject();

    public JsonArray ReadAsJsonArray() => ReadAsJsonNode().AsArray();

    public async Task OverwriteWithJson(JsonNode json, CancellationToken cancellationToken)
    {
        CreateDirectoryIfNotExists();

        using var stream = FileInfo.Open(FileMode.Create);
        var options = new JsonSerializerOptions { WriteIndented = true };

        await JsonSerializer.SerializeAsync(stream, json, options, cancellationToken);
    }

    public async Task OverwriteWithText(string text, CancellationToken cancellationToken)
    {
        CreateDirectoryIfNotExists();

        await File.WriteAllTextAsync(Path, text, cancellationToken);
    }

    public async Task OverwriteWithStream(Stream stream, CancellationToken cancellationToken)
    {
        CreateDirectoryIfNotExists();

        using var fileStream = FileInfo.Open(FileMode.Create);

        await stream.CopyToAsync(fileStream, cancellationToken);
    }

    private JsonNode ReadAsJsonNode()
    {
        using var stream = FileInfo.OpenRead();
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };

        return JsonNode.Parse(stream, options) ?? throw new InvalidOperationException($"Could not read JSON from file ${Path}.");
    }

    private void CreateDirectoryIfNotExists()
    {
        var directory = GetDirectoryInfo();

        directory.Create();
    }

    private DirectoryInfo GetDirectoryInfo()
    {
        return FileInfo.Directory
               ?? throw new InvalidOperationException($"Cannot find directory associated with file path {Path}.");
    }

    public static implicit operator FileInfo(FileRecord record) => record.FileInfo;
}

public abstract record DirectoryRecord
{
    public RecordPath Path { get; }

    private DirectoryInfo DirectoryInfo => new(Path);

    protected DirectoryRecord(RecordPath path)
    {
        Path = path;
    }

    public bool Exists() => DirectoryInfo.Exists;

    public static implicit operator DirectoryInfo(DirectoryRecord record) => record.DirectoryInfo;
}