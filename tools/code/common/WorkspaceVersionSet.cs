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

public sealed record WorkspaceVersionSetName : ResourceName, IResourceName<WorkspaceVersionSetName>
{
    private WorkspaceVersionSetName(string value) : base(value) { }

    public static WorkspaceVersionSetName From(string value) => new(value);
}

public sealed record WorkspaceVersionSetsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "apiVersionSets";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceVersionSetsUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceVersionSetUri : ResourceUri
{
    public required WorkspaceVersionSetsUri Parent { get; init; }

    public required WorkspaceVersionSetName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceVersionSetUri From(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceVersionSetsUri.From(workspaceName, serviceUri),
            Name = workspaceVersionSetName
        };
}

public sealed record WorkspaceVersionSetsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "version sets";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceVersionSetsDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceVersionSetsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceVersionSetsDirectory { Parent = parent }
            : Option<WorkspaceVersionSetsDirectory>.None;
}

public sealed record WorkspaceVersionSetDirectory : ResourceDirectory
{
    public required WorkspaceVersionSetsDirectory Parent { get; init; }

    public required WorkspaceVersionSetName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceVersionSetDirectory From(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceVersionSetsDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceVersionSetName
        };

    public static Option<WorkspaceVersionSetDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceVersionSetDirectory>.None
            : from parent in WorkspaceVersionSetsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceVersionSetName.From(directory.Name)
              select new WorkspaceVersionSetDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceVersionSetInformationFile : ResourceFile
{
    public required WorkspaceVersionSetDirectory Parent { get; init; }

    private static string Name { get; } = "versionSetInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceVersionSetInformationFile From(WorkspaceVersionSetName workspaceVersionSetName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceVersionSetDirectory.From(workspaceVersionSetName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceVersionSetInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceVersionSetDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceVersionSetInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceVersionSetInformationFile>.None;
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
    public static IAsyncEnumerable<WorkspaceVersionSetName> ListNames(this WorkspaceVersionSetsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceVersionSetName.From);

    public static IAsyncEnumerable<(WorkspaceVersionSetName Name, WorkspaceVersionSetDto Dto)> List(this WorkspaceVersionSetsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceVersionSetUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
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

    public static IEnumerable<WorkspaceVersionSetDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceVersionSetsDirectory = new WorkspaceVersionSetsDirectory { Parent = workspaceDirectory }
        where workspaceVersionSetsDirectory.ToDirectoryInfo().Exists()
        from workspaceVersionSetDirectoryInfo in workspaceVersionSetsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceVersionSetName.From(workspaceVersionSetDirectoryInfo.Name)
        select new WorkspaceVersionSetDirectory
        {
            Parent = workspaceVersionSetsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceVersionSetInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceVersionSetDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceVersionSetInformationFile { Parent = workspaceVersionSetDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

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