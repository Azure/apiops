using System;

namespace common;

public sealed record ProductApisUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductApisUri(ProductUri productUri)
    {
        Uri = productUri.AppendPath("apis");
    }
}

public sealed record ProductApiUri : IArtifactUri
{
    public Uri Uri { get; }

    public ProductApiUri(ApiName apiName, ProductApisUri productApisUri)
    {
        Uri = productApisUri.AppendPath(apiName.ToString());
    }
}

public sealed record ProductApisFile : IArtifactFile
{
    public static string Name { get; } = "apis.json";

    public ArtifactPath Path { get; }

    public ProductDirectory ProductDirectory { get; }

    public ProductApisFile(ProductDirectory productDirectory)
    {
        Path = productDirectory.Path.Append(Name);
        ProductDirectory = productDirectory;
    }
}