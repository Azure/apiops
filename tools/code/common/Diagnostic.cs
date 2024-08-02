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

public sealed record DiagnosticName : ResourceName, IResourceName<DiagnosticName>
{
    private DiagnosticName(string value) : base(value) { }

    public static DiagnosticName From(string value) => new(value);
}

public sealed record DiagnosticsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "diagnostics";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static DiagnosticsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record DiagnosticUri : ResourceUri
{
    public required DiagnosticsUri Parent { get; init; }
    public required DiagnosticName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static DiagnosticUri From(DiagnosticName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = DiagnosticsUri.From(serviceUri),
            Name = name
        };
}

public sealed record DiagnosticsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "diagnostics";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static DiagnosticsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<DiagnosticsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new DiagnosticsDirectory { ServiceDirectory = serviceDirectory }
            : Option<DiagnosticsDirectory>.None;
}

public sealed record DiagnosticDirectory : ResourceDirectory
{
    public required DiagnosticsDirectory Parent { get; init; }

    public required DiagnosticName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static DiagnosticDirectory From(DiagnosticName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = DiagnosticsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<DiagnosticDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in DiagnosticsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new DiagnosticDirectory
        {
            Parent = parent,
            Name = DiagnosticName.From(directory!.Name)
        };
}

public sealed record DiagnosticInformationFile : ResourceFile
{
    public required DiagnosticDirectory Parent { get; init; }
    private static string Name { get; } = "diagnosticInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static DiagnosticInformationFile From(DiagnosticName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new DiagnosticDirectory
            {
                Parent = DiagnosticsDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<DiagnosticInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in DiagnosticDirectory.TryParse(file.Directory, serviceDirectory)
              select new DiagnosticInformationFile { Parent = parent }
            : Option<DiagnosticInformationFile>.None;
}

public sealed record DiagnosticDto
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

public static class DiagnosticModule
{
    public static async ValueTask DeleteAll(this DiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await DiagnosticUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<DiagnosticName> ListNames(this DiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(DiagnosticName.From);

    public static IAsyncEnumerable<(DiagnosticName Name, DiagnosticDto Dto)> List(this DiagnosticsUri diagnosticsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        diagnosticsUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new DiagnosticUri { Parent = diagnosticsUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<DiagnosticDto>> TryGetDto(this DiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<DiagnosticDto>());
    }

    public static async ValueTask<DiagnosticDto> GetDto(this DiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<DiagnosticDto>();
    }

    public static async ValueTask Delete(this DiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this DiagnosticUri uri, DiagnosticDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<DiagnosticDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var diagnosticsDirectory = DiagnosticsDirectory.From(serviceDirectory);

        return diagnosticsDirectory.ToDirectoryInfo()
                                   .ListDirectories("*")
                                   .Select(directoryInfo => DiagnosticName.From(directoryInfo.Name))
                                   .Select(name => new DiagnosticDirectory { Parent = diagnosticsDirectory, Name = name });
    }

    public static IEnumerable<DiagnosticInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new DiagnosticInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this DiagnosticInformationFile file, DiagnosticDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<DiagnosticDto> ReadDto(this DiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<DiagnosticDto>();
    }

    public static Option<LoggerName> TryGetLoggerName(DiagnosticDto dto) =>
        from loggerId in Prelude.Optional(dto.Properties.LoggerId)
        from loggerNameString in loggerId.Split('/').LastOrNone()
        select LoggerName.From(loggerNameString);
}