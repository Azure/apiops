using System;

namespace common;

public sealed record ProductTagsUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductTagsUri(ProductUri productUri)
    {
        Uri = productUri.AppendPath("tags");
    }
}

public sealed record ProductTagUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductTagUri(TagName tagName, ProductTagsUri productTagsUri)
    {
        Uri = productTagsUri.AppendPath(tagName.ToString());
    }
}

public sealed record ProductTagsFile : IArtifactFile
{
    public static string Name { get; } = "tags.json";

    public ArtifactPath Path { get; }

    public ProductDirectory ProductDirectory { get; }

    public ProductTagsFile(ProductDirectory productDirectory)
    {
        Path = productDirectory.Path.Append(Name);
        ProductDirectory = productDirectory;
    }
}