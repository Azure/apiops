using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceTagName : ResourceName, IResourceName<WorkspaceTagName>
{
    private WorkspaceTagName(string value) : base(value) { }

    public static WorkspaceTagName From(string value) => new(value);
}

public sealed record WorkspaceTagsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "tags";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceTagsUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceTagUri : ResourceUri
{
    public required WorkspaceTagsUri Parent { get; init; }

    public required WorkspaceTagName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceTagUri From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceTagsUri.From(workspaceName, serviceUri),
            Name = workspaceTagName
        };
}

public sealed record WorkspaceTagsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "tags";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceTagsDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceTagsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceTagsDirectory { Parent = parent }
            : Option<WorkspaceTagsDirectory>.None;
}

public sealed record WorkspaceTagDirectory : ResourceDirectory
{
    public required WorkspaceTagsDirectory Parent { get; init; }

    public required WorkspaceTagName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceTagDirectory From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagsDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceTagName
        };

    public static Option<WorkspaceTagDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceTagDirectory>.None
            : from parent in WorkspaceTagsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceTagName.From(directory.Name)
              select new WorkspaceTagDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceTagInformationFile : ResourceFile
{
    public required WorkspaceTagDirectory Parent { get; init; }

    private static string Name { get; } = "tagInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceTagInformationFile From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagDirectory.From(workspaceTagName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceTagInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceTagDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceTagInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceTagInformationFile>.None;
}

public sealed record WorkspaceTagDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required TagContract Properties { get; init; }

    public sealed record TagContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }
    }
}

public static class WorkspaceTagModule
{
    public static IAsyncEnumerable<WorkspaceTagName> ListNames(this WorkspaceTagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceTagName.From);

    public static IAsyncEnumerable<(WorkspaceTagName Name, WorkspaceTagDto Dto)> List(this WorkspaceTagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceTagUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceTagDto> GetDto(this WorkspaceTagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceTagDto>();
    }

    public static async ValueTask Delete(this WorkspaceTagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceTagUri uri, WorkspaceTagDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceTagDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceTagsDirectory = new WorkspaceTagsDirectory { Parent = workspaceDirectory }
        where workspaceTagsDirectory.ToDirectoryInfo().Exists()
        from workspaceTagDirectoryInfo in workspaceTagsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceTagName.From(workspaceTagDirectoryInfo.Name)
        select new WorkspaceTagDirectory
        {
            Parent = workspaceTagsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceTagInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceTagDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceTagInformationFile { Parent = workspaceTagDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceTagInformationFile file, WorkspaceTagDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceTagDto> ReadDto(this WorkspaceTagInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceTagDto>();
    }
}