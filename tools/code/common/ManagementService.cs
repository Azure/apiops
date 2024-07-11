using System;
using System.IO;

namespace common;

public sealed record ManagementServiceName : ResourceName
{
    private ManagementServiceName(string value) : base(value) { }

    public static ManagementServiceName From(string value) => new(value);
}

public sealed record ManagementServiceDirectory : ResourceDirectory
{
    private ManagementServiceDirectory(DirectoryInfo value) => Value = value;

    protected override DirectoryInfo Value { get; }

    public static ManagementServiceDirectory From(DirectoryInfo value) => new(value);
}

public sealed record ManagementServiceProviderUri : ResourceUri
{
    private ManagementServiceProviderUri(Uri value) => Value = value;

    protected override Uri Value { get; }

    public static ManagementServiceProviderUri From(Uri value) => new(value);
}

public sealed record ManagementServiceUri : ResourceUri
{
    private ManagementServiceUri(Uri value) => Value = value;

    protected override Uri Value { get; }

    public static ManagementServiceUri From(Uri value) => new(value);
}