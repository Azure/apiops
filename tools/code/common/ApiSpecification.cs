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
    public ApiSpecificationFormat Format { get; }

    public ApiDirectory ApiDirectory { get; }

    private ApiSpecificationFile(ApiDirectory apiDirectory, ApiSpecificationFormat format)
        : base(apiDirectory.Path.Append(GetNameFromFormat(format)))
    {
        Format = format;

        ApiDirectory = apiDirectory;
    }

    public static ApiSpecificationFile From(ApiDirectory apiDirectory, ApiSpecificationFormat format) => new(apiDirectory, format);

    public static ApiSpecificationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        if (Enum.TryParse<ApiSpecificationFormat>(string.Concat(file.Extension.Skip(1)), ignoreCase: true, out var format))
        {
            if (GetNameFromFormat(format).Equals(file?.Name))
            {
                var apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory);

                return apiDirectory is null ? null : new(apiDirectory, format);
            }
            else
            {
                return null;
            }
        }
        else
        {
            return null;
        }
    }

    private static string GetNameFromFormat(ApiSpecificationFormat format) =>
        TryGetNameFromFormat(format) ?? throw new InvalidOperationException($"File format {format} is invalid.");

    internal static string? TryGetNameFromFormat(ApiSpecificationFormat format) =>
        format switch
        {
            ApiSpecificationFormat.Json => "specification.json",
            ApiSpecificationFormat.Yaml => "specification.yaml",
            _ => null
        };
}

public enum ApiSpecificationFormat
{
    Json,
    Yaml
}

public static class ApiSpecification
{
    internal static Uri GetUri(ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiSpecificationFormat format) =>
        Api.GetUri(serviceProviderUri, serviceName, apiName)
           .SetQueryParameter("export", "true")
           .SetQueryParameter("format", FormatToExportString(format));

    internal static string FormatToExportString(ApiSpecificationFormat format) =>
        format switch
        {
            ApiSpecificationFormat.Json => "openapi+json-link",
            ApiSpecificationFormat.Yaml => "openapi-link",
            _ => throw new InvalidOperationException($"File format {format} is invalid. Only OpenAPI YAML & JSON are supported.")
        };

    public static string FormatToString(ApiSpecificationFormat format) =>
        format switch
        {
            ApiSpecificationFormat.Json => "openapi+json",
            ApiSpecificationFormat.Yaml => "openapi",
            _ => throw new InvalidOperationException($"File format {format} is invalid. Only OpenAPI YAML & JSON are supported.")
        };

    public static async ValueTask<Stream> Get(Func<Uri, CancellationToken, ValueTask<JsonObject>> getResource, Func<Uri, CancellationToken, ValueTask<Stream>> downloader, ServiceProviderUri serviceProviderUri, ServiceName serviceName, ApiName apiName, ApiSpecificationFormat format, CancellationToken cancellationToken)
    {
        var uri = GetUri(serviceProviderUri, serviceName, apiName, format);
        var exportJson = await getResource(uri, cancellationToken);
        var downloadUrl = exportJson.GetJsonObjectProperty("value")
                                    .GetStringProperty("link");
        var downloadUri = new Uri(downloadUrl);

        return await downloader(downloadUri, cancellationToken);
    }

    public static ApiSpecificationFile? TryFindFile(ApiDirectory apiDirectory)
    {
        var directoryFileNames =
            apiDirectory.Exists()
            ? new DirectoryInfo(apiDirectory.Path).EnumerateFiles().Select(file => file.Name).ToList()
            : new List<string>();

        return
            Enum.GetValues<ApiSpecificationFormat>()
                .Where(format =>
                {
                    var formatFileName = ApiSpecificationFile.TryGetNameFromFormat(format);
                    return formatFileName is not null && directoryFileNames.Contains(formatFileName);
                })
                .Select(format => ApiSpecificationFile.From(apiDirectory, format))
                .FirstOrDefault();
    }

    public static async Task<ApiOperationName?> TryFindApiOperationName(ApiSpecificationFile file, ApiOperationDisplayName displayName)
    {
        using var stream = file.ReadAsStream();
        var readResult = await new OpenApiStreamReader().ReadAsync(stream);
        var operation = readResult.OpenApiDocument.Paths.Values.SelectMany(pathItem => pathItem.Operations.Values)
                                                               .FirstOrDefault(operation => operation.Summary.Equals((string)displayName, StringComparison.OrdinalIgnoreCase));

        return operation is null ? null : ApiOperationName.From(operation.OperationId);
    }
}