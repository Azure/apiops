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

public sealed record WorkspaceProductGroupsUri : ResourceUri
{
    public required WorkspaceProductUri Parent { get; init; }

    private static string PathSegment { get; } = "groupLinks";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceProductGroupsUri From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceProductUri.From(workspaceProductName, workspaceName, serviceUri) };
}

public sealed record WorkspaceProductGroupUri : ResourceUri
{
    public required WorkspaceProductGroupsUri Parent { get; init; }

    public required WorkspaceGroupName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceProductGroupUri From(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceProductGroupsUri.From(workspaceProductName, workspaceName, serviceUri),
            Name = workspaceGroupName
        };
}

public sealed record WorkspaceProductGroupsDirectory : ResourceDirectory
{
    public required WorkspaceProductDirectory Parent { get; init; }

    private static string Name { get; } = "groups";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceProductGroupsDirectory From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceProductDirectory.From(workspaceProductName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceProductGroupsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceProductDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceProductGroupsDirectory { Parent = parent }
            : Option<WorkspaceProductGroupsDirectory>.None;
}

public sealed record WorkspaceProductGroupDirectory : ResourceDirectory
{
    public required WorkspaceProductGroupsDirectory Parent { get; init; }

    public required WorkspaceGroupName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceProductGroupDirectory From(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductGroupsDirectory.From(workspaceProductName, workspaceName, serviceDirectory),
            Name = workspaceGroupName
        };

    public static Option<WorkspaceProductGroupDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceProductGroupDirectory>.None
            : from parent in WorkspaceProductGroupsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceGroupName.From(directory.Name)
              select new WorkspaceProductGroupDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceProductGroupInformationFile : ResourceFile
{
    public required WorkspaceProductGroupDirectory Parent { get; init; }

    private static string Name { get; } = "groupInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceProductGroupInformationFile From(WorkspaceGroupName workspaceGroupName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductGroupDirectory.From(workspaceGroupName, workspaceProductName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceProductGroupInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceProductGroupDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceProductGroupInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceProductGroupInformationFile>.None;
}

public sealed record WorkspaceProductGroupDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required GroupContract Properties { get; init; }

    public record GroupContract
    {
        [JsonPropertyName("groupId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? GroupId { get; init; }
    }
}

public static class WorkspaceProductGroupModule
{
    public static IAsyncEnumerable<WorkspaceGroupName> ListNames(this WorkspaceProductGroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceGroupName.From);

    public static IAsyncEnumerable<(WorkspaceGroupName Name, WorkspaceProductGroupDto Dto)> List(this WorkspaceProductGroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceProductGroupUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceProductGroupDto> GetDto(this WorkspaceProductGroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductGroupDto>();
    }

    public static async ValueTask Delete(this WorkspaceProductGroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceProductGroupUri uri, WorkspaceProductGroupDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceProductGroupDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceProductDirectory in WorkspaceProductModule.ListDirectories(serviceDirectory)
        let workspaceProductGroupsDirectory = new WorkspaceProductGroupsDirectory { Parent = workspaceProductDirectory }
        where workspaceProductGroupsDirectory.ToDirectoryInfo().Exists()
        from workspaceProductGroupDirectoryInfo in workspaceProductGroupsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceGroupName.From(workspaceProductGroupDirectoryInfo.Name)
        select new WorkspaceProductGroupDirectory
        {
            Parent = workspaceProductGroupsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceProductGroupInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceProductGroupDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceProductGroupInformationFile { Parent = workspaceProductGroupDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceProductGroupInformationFile file, WorkspaceProductGroupDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceProductGroupDto> ReadDto(this WorkspaceProductGroupInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductGroupDto>();
    }
}