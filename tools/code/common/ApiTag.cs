using System;

namespace common;

public sealed record ApiTagsUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiTagsUri(ApiUri apiUri)
    {
        Uri = apiUri.AppendPath("tags");
    }
}

public sealed record ApiTagUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiTagUri(TagName tagName, ApiTagsUri apiTagsUri)
    {
        Uri = apiTagsUri.AppendPath(tagName.ToString());
    }
}

public sealed record ApiTagsFile : IArtifactFile
{
    public static string Name { get; } = "tags.json";

    public ArtifactPath Path { get; }

    public ApiDirectory ApiDirectory { get; }

    public ApiTagsFile(ApiDirectory apiDirectory)
    {
        Path = apiDirectory.Path.Append(Name);
        ApiDirectory = apiDirectory;
    }
}