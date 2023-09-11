using System;

namespace common;

public sealed record ApiOperationsUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiOperationsUri(ApiUri apiUri)
    {
        Uri = apiUri.AppendPath("operations");
    }
}

public sealed record ApiOperationsDirectory : IArtifactDirectory
{
    public static string Name { get; } = "operations";

    public ArtifactPath Path { get; }

    public ApiDirectory ApiDirectory { get; }

    public ApiOperationsDirectory(ApiDirectory apiDirectory)
    {
        Path = apiDirectory.Path.Append(Name);
        ApiDirectory = apiDirectory;
    }
}

public sealed record ApiOperationName
{
    private readonly string value;

    public ApiOperationName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"API operation name cannot be null or whitespace.", nameof(value));
        }

        this.value = value;
    }

    public override string ToString() => value;
}

public sealed record ApiOperationUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiOperationUri(ApiOperationName apiOperationName, ApiOperationsUri apiOperationsUri)
    {
        Uri = apiOperationsUri.AppendPath(apiOperationName.ToString());
    }
}

public sealed record ApiOperationDirectory : IArtifactDirectory
{
    public ArtifactPath Path { get; }

    public ApiOperationsDirectory ApiOperationsDirectory { get; }

    public ApiOperationDirectory(ApiOperationName name, ApiOperationsDirectory apiOperationsDirectory)
    {
        Path = apiOperationsDirectory.Path.Append(name.ToString());
        ApiOperationsDirectory = apiOperationsDirectory;
    }
}