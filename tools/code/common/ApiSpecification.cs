using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiSpecificationFile : FileRecord
{
    public OpenApiSpecification Specification { get; }

    public ApiDirectory ApiDirectory { get; }

    private ApiSpecificationFile(ApiDirectory apiDirectory, OpenApiSpecification specification)
        : base(apiDirectory.Path.Append(specification.FileName))
    {
        Specification = specification;

        ApiDirectory = apiDirectory;
    }

    public static ApiSpecificationFile From(ApiDirectory apiDirectory, OpenApiSpecification specification) => new(apiDirectory, specification);

    public static ApiSpecificationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory);
        if (apiDirectory is null)
        {
            return null;
        }

        if (file.Name == OpenApiSpecification.V2Json.FileName
            || file.Name == OpenApiSpecification.V2Yaml.FileName
            || file.Name == OpenApiSpecification.V3Json.FileName
            || file.Name == OpenApiSpecification.V3Yaml.FileName)
        {
            var specification = OpenApiSpecification.TryFromFile(file);

            return specification is null
                ? null
                : new ApiSpecificationFile(apiDirectory, specification);
        }
        else
        {
            return null;
        }
    }
}

public record OpenApiSpecification
{
    private OpenApiSpecification(OpenApiSpecVersion version, OpenApiFormat format)
    {
        Version = version;
        Format = format;
    }

    public OpenApiSpecVersion Version { get; }
    public OpenApiFormat Format { get; }

    public string FileName => Format switch
    {
        OpenApiFormat.Json => "specification.json",
        OpenApiFormat.Yaml => "specification.yaml",
        _ => throw new NotSupportedException()
    };

    public static OpenApiSpecification V2Json { get; } = new(OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Json);
    public static OpenApiSpecification V2Yaml { get; } = new(OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Yaml);
    public static OpenApiSpecification V3Json { get; } = new(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Json);
    public static OpenApiSpecification V3Yaml { get; } = new(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml);

    public static OpenApiSpecification? TryFromFile(FileInfo file)
    {
        var version = TryGetOpenApiSpecVersion(file);
        if (version is null)
        {
            return null;
        }

        var format = TryGetOpenApiFormat(file);
        if (format is null)
        {
            return null;
        }

        return new OpenApiSpecification(version.Value, format.Value);
    }

    private static OpenApiSpecVersion? TryGetOpenApiSpecVersion(FileInfo file)
    {
        try
        {
            var _ = new OpenApiStreamReader().Read(file.OpenRead(), out var diagnostic);
            return diagnostic.SpecificationVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OpenApiFormat? TryGetOpenApiFormat(FileInfo file)
    {
        return file.Extension switch
        {
            ".json" => OpenApiFormat.Json,
            ".yaml" => OpenApiFormat.Yaml,
            ".yml" => OpenApiFormat.Yaml,
            _ => null
        };
    }
}

public static class ApiSpecification
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, OpenApiSpecification specification)
    {
        var exportQueryParameter = specification switch
        {
            _ when specification == OpenApiSpecification.V3Json => "openapi+json-link",
            _ when specification == OpenApiSpecification.V3Yaml => "openapi-link",
            _ when specification == OpenApiSpecification.V2Json => "swagger-link-json",
            _ => throw new NotImplementedException()
        };

        return Api.GetUri(serviceProviderUri, serviceName, apiName)
           .SetQueryParameter("export", "true")
           .SetQueryParameter("format", exportQueryParameter);
    }

    public static string GetApiPropertiesFormat(OpenApiSpecification specification) =>
        specification switch
        {
            _ when specification == OpenApiSpecification.V3Json => "openapi+json",
            _ when specification == OpenApiSpecification.V3Yaml => "openapi",
            _ when specification == OpenApiSpecification.V2Json => "swagger-json",
            _ => throw new NotImplementedException()
        };

    public static async ValueTask<Stream> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, Func<Uri, CancellationToken, ValueTask<Stream>> downloader, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, OpenApiSpecification specification, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri,
                         serviceName,
                         apiName,
                         // APIM doesn't support downloading Swagger YAML. We'll convert to Swagger JSON if needed.
                         specification == OpenApiSpecification.V2Yaml ? OpenApiSpecification.V2Json : specification);

        var exportJson = await getResource(uri, cancellationToken);

        var downloadUrl = exportJson.GetJsonObjectProperty("value")
                                    .GetStringProperty("link");

        var downloadUri = new Uri(downloadUrl);
        var stream = await downloader(downloadUri, cancellationToken);

        // Convert downloaded stream to Swagger YAML if needed
        if (specification == OpenApiSpecification.V2Yaml)
        {
            var readResult = await new OpenApiStreamReader().ReadAsync(stream);
            var memoryStream = new MemoryStream();
            readResult.OpenApiDocument.Serialize(memoryStream, OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Yaml);
            memoryStream.Position = 0;

            return memoryStream;
        }
        else
        {
            return stream;
        }
    }

    public static ApiSpecificationFile? TryFindFile(ApiDirectory apiDirectory)
    {
        var directoryFiles =
            apiDirectory.Exists()
            ? new DirectoryInfo(apiDirectory.Path).EnumerateFiles().ToList()
            : new List<FileInfo>();

        return directoryFiles.Choose(file => OpenApiSpecification.TryFromFile(file))
                             .Select(specification => ApiSpecificationFile.From(apiDirectory, specification))
                             .FirstOrDefault();
    }

    public static async ValueTask<ApiOperationName?> TryFindApiOperationName(ApiSpecificationFile file, ApiOperationDisplayName displayName)
    {
        using var stream = file.ReadAsStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(stream);
        var operation = readResult.OpenApiDocument.Paths.Values.SelectMany(pathItem => pathItem.Operations.Values)
                                                               .FirstOrDefault(operation => operation.Summary.Equals((string)displayName, StringComparison.OrdinalIgnoreCase));

        return operation is null ? null : ApiOperationName.From(operation.OperationId);
    }

    public static async ValueTask<string> GetFileContentsAsSpecification(ApiSpecificationFile file, OpenApiSpecification specification)
    {
        using var stream = file.ReadAsStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(stream);
        return readResult.OpenApiDocument.Serialize(specification.Version, specification.Format);
    }
}