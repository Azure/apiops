using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceDiagnosticsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "diagnostics";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceDiagnosticsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceDiagnosticUri : ResourceUri
{
    public required WorkspaceDiagnosticsUri Parent { get; init; }
    public required DiagnosticName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceDiagnosticUri From(DiagnosticName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceDiagnosticsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceDiagnosticsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "diagnostics";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceDiagnosticsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceDiagnosticsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceDiagnosticsDirectory { Parent = parent }
            : Option<WorkspaceDiagnosticsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceDiagnosticDirectory : ResourceDirectory
{
    public required WorkspaceDiagnosticsDirectory Parent { get; init; }

    public required DiagnosticName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceDiagnosticDirectory From(DiagnosticName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceDiagnosticsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceDiagnosticDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceDiagnosticsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceDiagnosticDirectory
        {
            Parent = parent,
            Name = DiagnosticName.From(directory!.Name)
        };
}

public sealed record WorkspaceDiagnosticInformationFile : ResourceFile
{
    public required WorkspaceDiagnosticDirectory Parent { get; init; }

    private static string Name { get; } = "diagnosticInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceDiagnosticInformationFile From(DiagnosticName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceDiagnosticDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceDiagnosticInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceDiagnosticDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceDiagnosticInformationFile { Parent = parent }
            : Option<WorkspaceDiagnosticInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record WorkspaceDiagnosticDto
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

public static class WorkspaceDiagnosticModule
{
    public static IAsyncEnumerable<DiagnosticName> ListNames(this WorkspaceDiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(DiagnosticName.From);

    public static IAsyncEnumerable<(DiagnosticName Name, WorkspaceDiagnosticDto Dto)> List(this WorkspaceDiagnosticsUri workspaceDiagnosticsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceDiagnosticsUri.ListNames(pipeline, cancellationToken)
                               .SelectAwait(async name =>
                               {
                                   var uri = new WorkspaceDiagnosticUri { Parent = workspaceDiagnosticsUri, Name = name };
                                   var dto = await uri.GetDto(pipeline, cancellationToken);
                                   return (name, dto);
                               });

    public static async ValueTask<WorkspaceDiagnosticDto> GetDto(this WorkspaceDiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceDiagnosticDto>();
    }

    public static async ValueTask Delete(this WorkspaceDiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceDiagnosticUri uri, WorkspaceDiagnosticDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static async ValueTask WriteDto(this WorkspaceDiagnosticInformationFile file, WorkspaceDiagnosticDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceDiagnosticDto> ReadDto(this WorkspaceDiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceDiagnosticDto>();
    }
}