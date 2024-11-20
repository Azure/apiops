using System;
using System.IO;

namespace common;

public abstract record ResourceName
{
    private readonly string value;

    protected ResourceName(string value) =>
        this.value = value;

    public sealed override string ToString() =>
        value;

    public virtual bool Equals(ResourceName? other) => value.Equals(other?.value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => value.GetHashCode(StringComparison.OrdinalIgnoreCase);
}

public abstract record ResourceDirectory
{
    private readonly string path;

    protected ResourceDirectory(string path) =>
        this.path = path;

    public DirectoryInfo ToDirectoryInfo() =>
        new(path);

    public sealed override string ToString() =>
        path;
}

public abstract record ResourceFile
{
    private readonly string path;

    protected ResourceFile(string path) =>
        this.path = path;

    public FileInfo ToFileInfo() =>
        new(path);

    public sealed override string ToString() =>
        path;
}

public abstract record ResourceUri
{
    private readonly Uri value;

    protected ResourceUri(Uri value) =>
        this.value = value;

    public sealed override string ToString() =>
        value.ToString();

    public Uri ToUri() => value;

    public virtual bool Equals(ResourceUri? other) => value.Equals(other?.value);

    public override int GetHashCode() => value.GetHashCode();
}

public static class ResourceDirectoryExtensions
{
    public static DirectoryInfo GetChildDirectory(this ResourceDirectory parent, string name) =>
         parent.ToDirectoryInfo().GetChildDirectory(name);
}