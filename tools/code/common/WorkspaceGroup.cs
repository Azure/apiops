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

public sealed record WorkspaceGroupName : ResourceName, IResourceName<WorkspaceGroupName>
{
    private WorkspaceGroupName(string value) : base(value) { }

    public static WorkspaceGroupName From(string value) => new(value);
}

public sealed record WorkspaceGroupsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "groups";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceGroupsUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceGroupUri : ResourceUri
{
    public required WorkspaceGroupsUri Parent { get; init; }

    public required WorkspaceGroupName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceGroupUri From(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceGroupsUri.From(workspaceName, serviceUri),
            Name = workspaceGroupName
        };
}

public sealed record WorkspaceGroupsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "groups";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceGroupsDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceGroupsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceGroupsDirectory { Parent = parent }
            : Option<WorkspaceGroupsDirectory>.None;
}

public sealed record WorkspaceGroupDirectory : ResourceDirectory
{
    public required WorkspaceGroupsDirectory Parent { get; init; }

    public required WorkspaceGroupName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceGroupDirectory From(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceGroupsDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceGroupName
        };

    public static Option<WorkspaceGroupDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceGroupDirectory>.None
            : from parent in WorkspaceGroupsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceGroupName.From(directory.Name)
              select new WorkspaceGroupDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceGroupInformationFile : ResourceFile
{
    public required WorkspaceGroupDirectory Parent { get; init; }

    private static string Name { get; } = "groupInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceGroupInformationFile From(WorkspaceGroupName workspaceGroupName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceGroupDirectory.From(workspaceGroupName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceGroupInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceGroupDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceGroupInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceGroupInformationFile>.None;
}

public sealed record WorkspaceGroupDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required GroupContract Properties { get; init; }

    public sealed record GroupContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("externalId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ExternalId { get; init; }

        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Type { get; init; }
    }
}

public static class WorkspaceGroupModule
{
    public static IAsyncEnumerable<WorkspaceGroupName> ListNames(this WorkspaceGroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceGroupName.From);

    public static IAsyncEnumerable<(WorkspaceGroupName Name, WorkspaceGroupDto Dto)> List(this WorkspaceGroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceGroupUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceGroupDto> GetDto(this WorkspaceGroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceGroupDto>();
    }

    public static async ValueTask Delete(this WorkspaceGroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceGroupUri uri, WorkspaceGroupDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceGroupDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceGroupsDirectory = new WorkspaceGroupsDirectory { Parent = workspaceDirectory }
        where workspaceGroupsDirectory.ToDirectoryInfo().Exists()
        from workspaceGroupDirectoryInfo in workspaceGroupsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceGroupName.From(workspaceGroupDirectoryInfo.Name)
        select new WorkspaceGroupDirectory
        {
            Parent = workspaceGroupsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceGroupInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceGroupDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceGroupInformationFile { Parent = workspaceGroupDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceGroupInformationFile file, WorkspaceGroupDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceGroupDto> ReadDto(this WorkspaceGroupInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceGroupDto>();
    }
}