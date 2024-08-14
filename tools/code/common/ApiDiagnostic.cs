using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiDiagnosticName : ResourceName, IResourceName<ApiDiagnosticName>
{
    private ApiDiagnosticName(string value) : base(value) { }

    public static ApiDiagnosticName From(string value) => new(value);
}

public sealed record ApiDiagnosticsUri : ResourceUri
{
    public required ApiUri Parent { get; init; }

    private static string PathSegment { get; } = "diagnostics";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ApiDiagnosticsUri From(ApiName apiName, ManagementServiceUri serviceUri) =>
        new() { Parent = ApiUri.From(apiName, serviceUri) };
}

public sealed record ApiDiagnosticUri : ResourceUri
{
    public required ApiDiagnosticsUri Parent { get; init; }

    public required ApiDiagnosticName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ApiDiagnosticUri From(ApiDiagnosticName name, ApiName apiName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ApiDiagnosticsUri.From(apiName, serviceUri),
            Name = name
        };
}

public sealed record ApiDiagnosticsDirectory : ResourceDirectory
{
    public required ApiDirectory Parent { get; init; }

    private static string Name { get; } = "diagnostics";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ApiDiagnosticsDirectory From(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(apiName, serviceDirectory) };

    public static Option<ApiDiagnosticsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in ApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ApiDiagnosticsDirectory { Parent = parent }
            : Option<ApiDiagnosticsDirectory>.None;
}

public sealed record ApiDiagnosticDirectory : ResourceDirectory
{
    public required ApiDiagnosticsDirectory Parent { get; init; }

    public required ApiDiagnosticName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static ApiDiagnosticDirectory From(ApiDiagnosticName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiDiagnosticsDirectory.From(apiName, serviceDirectory),
            Name = name
        };

    public static Option<ApiDiagnosticDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in ApiDiagnosticsDirectory.TryParse(directory?.Parent, serviceDirectory)
        let name = ApiDiagnosticName.From(directory!.Name)
        select new ApiDiagnosticDirectory
        {
            Parent = parent,
            Name = name
        };
}

public sealed record ApiDiagnosticInformationFile : ResourceFile
{
    public required ApiDiagnosticDirectory Parent { get; init; }

    private static string Name { get; } = "diagnosticInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ApiDiagnosticInformationFile From(ApiDiagnosticName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiDiagnosticDirectory.From(name, apiName, serviceDirectory)
        };

    public static Option<ApiDiagnosticInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in ApiDiagnosticDirectory.TryParse(file.Directory, serviceDirectory)
              select new ApiDiagnosticInformationFile
              {
                  Parent = parent
              }
            : Option<ApiDiagnosticInformationFile>.None;
}

public sealed record ApiDiagnosticDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required DiagnosticContract Properties { get; init; }

    public sealed record DiagnosticContract
    {
        [JsonPropertyName("loggerId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LoggerId { get; init; }

        [JsonPropertyName("alwaysLog")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? AlwaysLog { get; init; }

        [JsonPropertyName("backend")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public PipelineDiagnosticSettings? Backend { get; init; }

        [JsonPropertyName("frontend")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public PipelineDiagnosticSettings? Frontend { get; init; }

        [JsonPropertyName("httpCorrelationProtocol")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? HttpCorrelationProtocol { get; init; }

        [JsonPropertyName("logClientIp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? LogClientIp { get; init; }

        [JsonPropertyName("metrics")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? Metrics { get; init; }

        [JsonPropertyName("operationNameFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? OperationNameFormat { get; init; }

        [JsonPropertyName("sampling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public SamplingSettings? Sampling { get; init; }

        [JsonPropertyName("verbosity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Verbosity { get; init; }
    }

    public sealed record PipelineDiagnosticSettings
    {
        [JsonPropertyName("request")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public HttpMessageDiagnostic? Request { get; init; }

        [JsonPropertyName("response")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public HttpMessageDiagnostic? Response { get; init; }
    }

    public sealed record HttpMessageDiagnostic
    {
        [JsonPropertyName("body")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BodyDiagnosticSettings? Body { get; init; }

        [JsonPropertyName("dataMasking")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public DataMasking? DataMasking { get; init; }

        [JsonPropertyName("headers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Headers { get; init; }
    }

    public sealed record BodyDiagnosticSettings
    {
        [JsonPropertyName("bytes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? Bytes { get; init; }
    }

    public sealed record DataMasking
    {
        [JsonPropertyName("headers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<DataMaskingEntity>? Headers { get; init; }

        [JsonPropertyName("queryParams")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<DataMaskingEntity>? QueryParams { get; init; }
    }

    public sealed record DataMaskingEntity
    {
        [JsonPropertyName("mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Mode { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }

    public sealed record SamplingSettings
    {
        [JsonPropertyName("percentage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float? Percentage { get; init; }

        [JsonPropertyName("samplingType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SamplingType { get; init; }
    }
}

public static class ApiDiagnosticModule
{
    public static async ValueTask DeleteAll(this ApiDiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var resourceUri = new ApiDiagnosticUri { Parent = uri, Name = name };
                     await resourceUri.Delete(pipeline, cancellationToken);
                 }, cancellationToken);

    public static IAsyncEnumerable<ApiDiagnosticName> ListNames(this ApiDiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiDiagnosticName.From);

    public static IAsyncEnumerable<(ApiDiagnosticName Name, ApiDiagnosticDto Dto)> List(this ApiDiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new ApiDiagnosticUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<ApiDiagnosticDto> GetDto(this ApiDiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<ApiDiagnosticDto>();
    }

    public static async ValueTask Delete(this ApiDiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ApiDiagnosticUri uri, ApiDiagnosticDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ApiDiagnosticDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from apiDirectory in ApiModule.ListDirectories(serviceDirectory)
        let diagnosticsDirectory = new ApiDiagnosticsDirectory { Parent = apiDirectory }
        where diagnosticsDirectory.ToDirectoryInfo().Exists()
        from diagnosticDirectoryInfo in diagnosticsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = ApiDiagnosticName.From(diagnosticDirectoryInfo.Name)
        select new ApiDiagnosticDirectory
        {
            Parent = diagnosticsDirectory,
            Name = name
        };

    public static IEnumerable<ApiDiagnosticInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from diagnosticDirectory in ListDirectories(serviceDirectory)
        let informationFile = new ApiDiagnosticInformationFile { Parent = diagnosticDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this ApiDiagnosticInformationFile file, ApiDiagnosticDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ApiDiagnosticDto> ReadDto(this ApiDiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ApiDiagnosticDto>();
    }
}
