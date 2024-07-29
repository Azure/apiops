using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceGroupsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "groups";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceGroupsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceGroupUri : ResourceUri
{
    public required WorkspaceGroupsUri Parent { get; init; }
    public required GroupName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceGroupUri From(GroupName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceGroupsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceGroupsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "groups";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceGroupsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceGroupsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceGroupsDirectory { Parent = parent }
            : Option<WorkspaceGroupsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceGroupDirectory : ResourceDirectory
{
    public required WorkspaceGroupsDirectory Parent { get; init; }

    public required GroupName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceGroupDirectory From(GroupName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceGroupsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceGroupDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceGroupsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceGroupDirectory
        {
            Parent = parent,
            Name = GroupName.From(directory!.Name)
        };
}

public sealed record WorkspaceGroupInformationFile : ResourceFile
{
    public required WorkspaceGroupDirectory Parent { get; init; }

    private static string Name { get; } = "groupInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceGroupInformationFile From(GroupName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceGroupDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceGroupInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceGroupDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceGroupInformationFile { Parent = parent }
            : Option<WorkspaceGroupInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
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
    public static IAsyncEnumerable<GroupName> ListNames(this WorkspaceGroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(GroupName.From);

    public static IAsyncEnumerable<(GroupName Name, WorkspaceGroupDto Dto)> List(this WorkspaceGroupsUri workspaceGroupsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceGroupsUri.ListNames(pipeline, cancellationToken)
                          .SelectAwait(async name =>
                          {
                              var uri = new WorkspaceGroupUri { Parent = workspaceGroupsUri, Name = name };
                              var dto = await uri.GetDto(pipeline, cancellationToken);
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