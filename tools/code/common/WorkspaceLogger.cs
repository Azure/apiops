using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceLoggerName : ResourceName, IResourceName<WorkspaceLoggerName>
{
    private WorkspaceLoggerName(string value) : base(value) { }

    public static WorkspaceLoggerName From(string value) => new(value);
}

public sealed record WorkspaceLoggersUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "loggers";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceLoggersUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceLoggerUri : ResourceUri
{
    public required WorkspaceLoggersUri Parent { get; init; }

    public required WorkspaceLoggerName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceLoggerUri From(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceLoggersUri.From(workspaceName, serviceUri),
            Name = workspaceLoggerName
        };
}

public sealed record WorkspaceLoggersDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "loggers";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceLoggersDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceLoggersDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceLoggersDirectory { Parent = parent }
            : Option<WorkspaceLoggersDirectory>.None;
}

public sealed record WorkspaceLoggerDirectory : ResourceDirectory
{
    public required WorkspaceLoggersDirectory Parent { get; init; }

    public required WorkspaceLoggerName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceLoggerDirectory From(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceLoggersDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceLoggerName
        };

    public static Option<WorkspaceLoggerDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceLoggerDirectory>.None
            : from parent in WorkspaceLoggersDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceLoggerName.From(directory.Name)
              select new WorkspaceLoggerDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceLoggerInformationFile : ResourceFile
{
    public required WorkspaceLoggerDirectory Parent { get; init; }

    private static string Name { get; } = "loggerInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceLoggerInformationFile From(WorkspaceLoggerName workspaceLoggerName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceLoggerDirectory.From(workspaceLoggerName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceLoggerInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceLoggerDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceLoggerInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceLoggerInformationFile>.None;
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
    public static IAsyncEnumerable<WorkspaceLoggerName> ListNames(this WorkspaceLoggersUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceLoggerName.From);

    public static IAsyncEnumerable<(WorkspaceLoggerName Name, WorkspaceLoggerDto Dto)> List(this WorkspaceLoggersUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceLoggerUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
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

    public static IEnumerable<WorkspaceLoggerDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceLoggersDirectory = new WorkspaceLoggersDirectory { Parent = workspaceDirectory }
        where workspaceLoggersDirectory.ToDirectoryInfo().Exists()
        from workspaceLoggerDirectoryInfo in workspaceLoggersDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceLoggerName.From(workspaceLoggerDirectoryInfo.Name)
        select new WorkspaceLoggerDirectory
        {
            Parent = workspaceLoggersDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceLoggerInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceLoggerDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceLoggerInformationFile { Parent = workspaceLoggerDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

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