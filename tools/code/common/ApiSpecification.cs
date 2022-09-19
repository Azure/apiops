using Flurl;
using Microsoft.OpenApi;
using System;

namespace common;

public sealed record ApiSpecificationExportUri : IArtifactUri
{
    public Uri Uri { get; }

    public ApiSpecificationExportUri(ApiUri apiUri)
    {
        Uri = apiUri.Uri.SetQueryParam("format", "openapi-link")
                        .SetQueryParam("export", "true")
                        .SetQueryParam("api-version", "2021-08-01")
                        .ToUri();
    }
}

public sealed record ApiSpecificationFile : IArtifactFile
{
    public ArtifactPath Path { get; }

    public ApiDirectory ApiDirectory { get; }

    public OpenApiSpecVersion Version { get; }

    public OpenApiFormat Format { get; }

    public ApiSpecificationFile(OpenApiSpecVersion version, OpenApiFormat format, ApiDirectory apiDirectory)
    {
        var fileName = GetFileName(format);
        Path = apiDirectory.Path.Append(fileName);
        ApiDirectory = apiDirectory;
        Version = version;
        Format = format;
    }

    private static string GetFileName(OpenApiFormat format) =>
        format switch
        {
            OpenApiFormat.Json => "specification.json",
            OpenApiFormat.Yaml => "specification.yaml",
            _ => throw new NotSupportedException()
        };
}