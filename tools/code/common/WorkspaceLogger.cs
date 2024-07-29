using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceLoggersUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "loggers";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceLoggersUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceLoggerUri : ResourceUri
{
    public required WorkspaceLoggersUri Parent { get; init; }
    public required LoggerName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceLoggerUri From(LoggerName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceLoggersUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceLoggersDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "loggers";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceLoggersDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceLoggersDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceLoggersDirectory { Parent = parent }
            : Option<WorkspaceLoggersDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceLoggerDirectory : ResourceDirectory
{
    public required WorkspaceLoggersDirectory Parent { get; init; }

    public required LoggerName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceLoggerDirectory From(LoggerName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceLoggersDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceLoggerDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceLoggersDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceLoggerDirectory
        {
            Parent = parent,
            Name = LoggerName.From(directory!.Name)
        };
}

public sealed record WorkspaceLoggerInformationFile : ResourceFile
{
    public required WorkspaceLoggerDirectory Parent { get; init; }

    private static string Name { get; } = "loggerInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceLoggerInformationFile From(LoggerName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceLoggerDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceLoggerInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceLoggerDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceLoggerInformationFile { Parent = parent }
            : Option<WorkspaceLoggerInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record WorkspaceLoggerDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required LoggerContract Properties { get; init; }

    public record LoggerContract
    {
        [JsonPropertyName("loggerType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LoggerType { get; init; }

        [JsonPropertyName("credentials")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonObject? Credentials { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("isBuffered")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? IsBuffered { get; init; }

        [JsonPropertyName("resourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ResourceId { get; init; }
    }
}

public static class WorkspaceLoggerModule
{
    public static IAsyncEnumerable<LoggerName> ListNames(this WorkspaceLoggersUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(LoggerName.From);

    public static IAsyncEnumerable<(LoggerName Name, WorkspaceLoggerDto Dto)> List(this WorkspaceLoggersUri workspaceLoggersUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceLoggersUri.ListNames(pipeline, cancellationToken)
                           .SelectAwait(async name =>
                           {
                               var uri = new WorkspaceLoggerUri { Parent = workspaceLoggersUri, Name = name };
                               var dto = await uri.GetDto(pipeline, cancellationToken);
                               return (name, dto);
                           });

    public static async ValueTask<WorkspaceLoggerDto> GetDto(this WorkspaceLoggerUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceLoggerDto>();
    }

    public static async ValueTask Delete(this WorkspaceLoggerUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceLoggerUri uri, WorkspaceLoggerDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static async ValueTask WriteDto(this WorkspaceLoggerInformationFile file, WorkspaceLoggerDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceLoggerDto> ReadDto(this WorkspaceLoggerInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceLoggerDto>();
    }
}