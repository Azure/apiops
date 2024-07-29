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

public sealed record WorkspaceVersionSetsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "apiVersionSets";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceVersionSetsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceVersionSetUri : ResourceUri
{
    public required WorkspaceVersionSetsUri Parent { get; init; }
    public required VersionSetName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceVersionSetUri From(VersionSetName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceVersionSetsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceVersionSetsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "version sets";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceVersionSetsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceVersionSetsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceVersionSetsDirectory { Parent = parent }
            : Option<WorkspaceVersionSetsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceVersionSetDirectory : ResourceDirectory
{
    public required WorkspaceVersionSetsDirectory Parent { get; init; }

    public required VersionSetName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceVersionSetDirectory From(VersionSetName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceVersionSetsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceVersionSetDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceVersionSetsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceVersionSetDirectory
        {
            Parent = parent,
            Name = VersionSetName.From(directory!.Name)
        };
}

public sealed record WorkspaceVersionSetInformationFile : ResourceFile
{
    public required WorkspaceVersionSetDirectory Parent { get; init; }

    private static string Name { get; } = "versionSetInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceVersionSetInformationFile From(VersionSetName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceVersionSetDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceVersionSetInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceVersionSetDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceVersionSetInformationFile { Parent = parent }
            : Option<WorkspaceVersionSetInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record WorkspaceVersionSetDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required VersionSetContract Properties { get; init; }

    public sealed record VersionSetContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("versioningScheme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersioningScheme { get; init; }

        [JsonPropertyName("versionQueryName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersionQueryName { get; init; }

        [JsonPropertyName("versionHeaderName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersionHeaderName { get; init; }
    }
}

public static class WorkspaceVersionSetModule
{
    public static IAsyncEnumerable<VersionSetName> ListNames(this WorkspaceVersionSetsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(VersionSetName.From);

    public static IAsyncEnumerable<(VersionSetName Name, WorkspaceVersionSetDto Dto)> List(this WorkspaceVersionSetsUri workspaceVersionSetsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceVersionSetsUri.ListNames(pipeline, cancellationToken)
                               .SelectAwait(async name =>
                               {
                                   var uri = new WorkspaceVersionSetUri { Parent = workspaceVersionSetsUri, Name = name };
                                   var dto = await uri.GetDto(pipeline, cancellationToken);
                                   return (name, dto);
                               });

    public static async ValueTask<WorkspaceVersionSetDto> GetDto(this WorkspaceVersionSetUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceVersionSetDto>();
    }

    public static async ValueTask Delete(this WorkspaceVersionSetUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceVersionSetUri uri, WorkspaceVersionSetDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static async ValueTask WriteDto(this WorkspaceVersionSetInformationFile file, WorkspaceVersionSetDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceVersionSetDto> ReadDto(this WorkspaceVersionSetInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceVersionSetDto>();
    }
}