using System;

namespace common;

public sealed record ProductGroupsUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductGroupsUri(ProductUri productUri)
    {
        Uri = productUri.AppendPath("groups");
    }
}

public sealed record ProductGroupUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductGroupUri(GroupName groupName, ProductGroupsUri productGroupsUri)
    {
        Uri = productGroupsUri.AppendPath(groupName.ToString());
    }
}

public sealed record ProductGroupsFile : IArtifactFile
{
    public static string Name { get; } = "groups.json";

    public ArtifactPath Path { get; }

    public ProductDirectory ProductDirectory { get; }

    public ProductGroupsFile(ProductDirectory productDirectory)
    {
        Path = productDirectory.Path.Append(Name);
        ProductDirectory = productDirectory;
    }
}