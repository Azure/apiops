using System;
using System.IO;

namespace common;

public abstract record NonEmptyString
{
    protected NonEmptyString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
        Value = value;
    }

    public string Value { get; }

    public sealed override string ToString() => Value;
}

public abstract record ResourceName : NonEmptyString
{
    protected ResourceName(string value) : base(value) { }

    public virtual bool Equals(ResourceName? other) => string.Equals(Value, other?.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.OrdinalIgnoreCase);
}

public interface IResourceName<T>
{
    public abstract static T From(string value);
}

public abstract record ResourceDirectory
{
    protected abstract DirectoryInfo Value { get; }

    public sealed override string ToString() => Value.FullName;

    public DirectoryInfo ToDirectoryInfo() => Value;

    public virtual bool Equals(ResourceDirectory? other) => Value.FullName.Equals(other?.Value.FullName, StringComparison.Ordinal);

    public override int GetHashCode() => Value.FullName.GetHashCode(StringComparison.Ordinal);
}

public abstract record ResourceFile
{
    protected abstract FileInfo Value { get; }

    public sealed override string ToString() => Value.FullName;

    public FileInfo ToFileInfo() => Value;

    public virtual bool Equals(ResourceFile? other) => Value.FullName.Equals(other?.Value.FullName, StringComparison.Ordinal);

    public override int GetHashCode() => Value.FullName.GetHashCode(StringComparison.Ordinal);
}

public abstract record ResourceUri
{
    protected abstract Uri Value { get; }

    public sealed override string ToString() => Value.ToString();

    public Uri ToUri() => Value;

    public virtual bool Equals(ResourceUri? other) => Value.Equals(other?.Value);

    public override int GetHashCode() => Value.GetHashCode();
}