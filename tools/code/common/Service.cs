using System;
using System.IO;

namespace common;

public sealed record ServiceDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public ServiceDirectory(DirectoryInfo directory)
    {
        Path = new ArtifactPath(directory.FullName);
    }
}

public sealed record ServiceUri : IArtifactUri
{
    public Uri Uri { get; }

    public ServiceUri(Uri uri)
    {
        Uri = uri;
    }
}