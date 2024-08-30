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

public sealed record WorkspaceApiDiagnosticName : ResourceName, IResourceName<WorkspaceApiDiagnosticName>
{
    private WorkspaceApiDiagnosticName(string value) : base(value) { }

    public static WorkspaceApiDiagnosticName From(string value) => new(value);
}

public sealed record WorkspaceApiDiagnosticsUri : ResourceUri
{
    public required WorkspaceApiUri Parent { get; init; }

    private static string PathSegment { get; } = "diagnostics";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceApiDiagnosticsUri From(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceApiUri.From(workspaceApiName, workspaceName, serviceUri) };
}

public sealed record WorkspaceApiDiagnosticUri : ResourceUri
{
    public required WorkspaceApiDiagnosticsUri Parent { get; init; }

    public required WorkspaceApiDiagnosticName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceApiDiagnosticUri From(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceApiDiagnosticsUri.From(workspaceApiName, workspaceName, serviceUri),
            Name = workspaceApiDiagnosticName
        };
}

public sealed record WorkspaceApiDiagnosticsDirectory : ResourceDirectory
{
    public required WorkspaceApiDirectory Parent { get; init; }

    private static string Name { get; } = "diagnostics";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceApiDiagnosticsDirectory From(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceApiDirectory.From(workspaceApiName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceApiDiagnosticsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceApiDiagnosticsDirectory { Parent = parent }
            : Option<WorkspaceApiDiagnosticsDirectory>.None;
}

public sealed record WorkspaceApiDiagnosticDirectory : ResourceDirectory
{
    public required WorkspaceApiDiagnosticsDirectory Parent { get; init; }

    public required WorkspaceApiDiagnosticName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceApiDiagnosticDirectory From(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiDiagnosticsDirectory.From(workspaceApiName, workspaceName, serviceDirectory),
            Name = workspaceApiDiagnosticName
        };

    public static Option<WorkspaceApiDiagnosticDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceApiDiagnosticDirectory>.None
            : from parent in WorkspaceApiDiagnosticsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceApiDiagnosticName.From(directory.Name)
              select new WorkspaceApiDiagnosticDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceApiDiagnosticInformationFile : ResourceFile
{
    public required WorkspaceApiDiagnosticDirectory Parent { get; init; }

    private static string Name { get; } = "diagnosticInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceApiDiagnosticInformationFile From(WorkspaceApiDiagnosticName workspaceApiDiagnosticName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiDiagnosticDirectory.From(workspaceApiDiagnosticName, workspaceApiName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceApiDiagnosticInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceApiDiagnosticDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceApiDiagnosticInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceApiDiagnosticInformationFile>.None;
}

public sealed record WorkspaceApiDiagnosticDto
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

public static class WorkspaceApiDiagnosticModule
{
    public static IAsyncEnumerable<WorkspaceApiDiagnosticName> ListNames(this WorkspaceApiDiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiDiagnosticName.From);

    public static IAsyncEnumerable<(WorkspaceApiDiagnosticName Name, WorkspaceApiDiagnosticDto Dto)> List(this WorkspaceApiDiagnosticsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceApiDiagnosticUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceApiDiagnosticDto> GetDto(this WorkspaceApiDiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiDiagnosticDto>();
    }

    public static async ValueTask Delete(this WorkspaceApiDiagnosticUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceApiDiagnosticUri uri, WorkspaceApiDiagnosticDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceApiDiagnosticDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceApiDirectory in WorkspaceApiModule.ListDirectories(serviceDirectory)
        let workspaceApiDiagnosticsDirectory = new WorkspaceApiDiagnosticsDirectory { Parent = workspaceApiDirectory }
        where workspaceApiDiagnosticsDirectory.ToDirectoryInfo().Exists()
        from workspaceApiDiagnosticDirectoryInfo in workspaceApiDiagnosticsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceApiDiagnosticName.From(workspaceApiDiagnosticDirectoryInfo.Name)
        select new WorkspaceApiDiagnosticDirectory
        {
            Parent = workspaceApiDiagnosticsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceApiDiagnosticInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceApiDiagnosticDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceApiDiagnosticInformationFile { Parent = workspaceApiDiagnosticDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceApiDiagnosticInformationFile file, WorkspaceApiDiagnosticDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceApiDiagnosticDto> ReadDto(this WorkspaceApiDiagnosticInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiDiagnosticDto>();
    }
}