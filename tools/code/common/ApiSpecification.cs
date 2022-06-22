using Microsoft.OpenApi.Readers;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        : base(apiDirectory.Path.Append(format.FileName))
    {
        Format = format;

        ApiDirectory = apiDirectory;
    }

    public static ApiSpecificationFile From(ApiDirectory apiDirectory, ApiSpecificationFormat format) => new(apiDirectory, format);

    public static ApiSpecificationFile? TryFrom(ServiceDirectory serviceDirectory, FileInfo file)
    {
        var specificationFiles = from specificationFormat in ApiSpecificationFormat.List
                                 where specificationFormat.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)
                                 let apiDirectory = ApiDirectory.TryFrom(serviceDirectory, file.Directory)
                                 where apiDirectory is not null
                                 select new ApiSpecificationFile(apiDirectory, specificationFormat);

        return specificationFiles.FirstOrDefault();
    }
}

public abstract record ApiSpecificationFormat
{
    private ApiSpecificationFormat(string formatName, string fileName)
    {
        FormatName = formatName;
        FileName = fileName;
    }

    public string FormatName { get; }
    public string FileName { get; }

    public record OpenApi2Json : ApiSpecificationFormat
    {
        public OpenApi2Json() : base(formatName: nameof(OpenApi2Json), fileName: "specification.json") { }
    }

    public record OpenApi3Json : ApiSpecificationFormat
    {
        public OpenApi3Json() : base(formatName: nameof(OpenApi3Json), fileName: "specification.json") { }
    }

    public record OpenApi3Yaml : ApiSpecificationFormat
    {
        public OpenApi3Yaml() : base(formatName: nameof(OpenApi3Yaml), fileName: "specification.yaml") { }
    }

    public static ImmutableList<ApiSpecificationFormat> List { get; } =
        ImmutableList.Create<ApiSpecificationFormat>(new OpenApi2Json(),
                                                     new OpenApi3Json(),
                                                     new OpenApi3Yaml());
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
            ApiSpecificationFormat.OpenApi3Json => "openapi+json-link",
            ApiSpecificationFormat.OpenApi3Yaml => "openapi-link",
            ApiSpecificationFormat.OpenApi2Json => "swagger-link-json",
            _ => throw new NotImplementedException()
        };

    public static string FormatToString(ApiSpecificationFormat format) =>
        format switch
        {
            ApiSpecificationFormat.OpenApi3Json => "openapi+json",
            ApiSpecificationFormat.OpenApi3Yaml => "openapi",
            ApiSpecificationFormat.OpenApi2Json => "swagger-json",
            _ => throw new NotImplementedException()
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

        var specificationFormats = new List<ApiSpecificationFormat>
        {
            new ApiSpecificationFormat.OpenApi2Json(),
            new ApiSpecificationFormat.OpenApi3Json(),
            new ApiSpecificationFormat.OpenApi3Yaml(),
        };

        return ApiSpecificationFormat.List
                                     .Where(format => directoryFileNames.Contains(format.FileName, StringComparer.OrdinalIgnoreCase))
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