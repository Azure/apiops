using LanguageExt;
using System;
using System.IO;

namespace common;

public sealed record ServiceName : ResourceName
{
    private ServiceName(string value) : base(value) { }

    public static Fin<ServiceName> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? Fin<ServiceName>.Fail($"{typeof(ServiceName)} cannot be null or whitespace.")
        : new ServiceName(value);
}

public sealed record ServiceUri : ResourceUri
{
    private ServiceUri(Uri value) : base(value) { }
    public static ServiceUri From(Uri value) =>
        new(value);
}

public sealed record ServiceDirectory : ResourceDirectory
{
    private ServiceDirectory(string path) : base(path) { }

    public static ServiceDirectory From(DirectoryInfo value) =>
        new(value.FullName);
}