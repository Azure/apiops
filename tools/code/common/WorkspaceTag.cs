using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceTagsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "tags";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceTagsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceTagUri : ResourceUri
{
    public required WorkspaceTagsUri Parent { get; init; }
    public required TagName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceTagUri From(TagName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceTagsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceTagsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "tags";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceTagsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceTagsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceTagsDirectory { Parent = parent }
            : Option<WorkspaceTagsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceTagDirectory : ResourceDirectory
{
    public required WorkspaceTagsDirectory Parent { get; init; }

    public required TagName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceTagDirectory From(TagName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceTagDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceTagsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceTagDirectory
        {
            Parent = parent,
            Name = TagName.From(directory!.Name)
        };
}

public sealed record WorkspaceTagInformationFile : ResourceFile
{
    public required WorkspaceTagDirectory Parent { get; init; }

    private static string Name { get; } = "tagInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceTagInformationFile From(TagName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceTagInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceTagDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceTagInformationFile { Parent = parent }
            : Option<WorkspaceTagInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
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
    public static IAsyncEnumerable<TagName> ListNames(this WorkspaceTagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(TagName.From);

    public static IAsyncEnumerable<(TagName Name, WorkspaceTagDto Dto)> List(this WorkspaceTagsUri workspaceTagsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceTagsUri.ListNames(pipeline, cancellationToken)
                        .SelectAwait(async name =>
                        {
                            var uri = new WorkspaceTagUri { Parent = workspaceTagsUri, Name = name };
                            var dto = await uri.GetDto(pipeline, cancellationToken);
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