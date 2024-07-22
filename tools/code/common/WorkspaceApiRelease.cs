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

public sealed record WorkspaceApiReleaseName : ResourceName
{
    private WorkspaceApiReleaseName(string value) : base(value) { }

    public static WorkspaceApiReleaseName From(string value) => new(value);
}

public sealed record WorkspaceApiReleasesUri : ResourceUri
{
    public required WorkspaceApiUri Parent { get; init; }

    private static string PathSegment { get; } = "releases";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceApiReleasesUri From(ApiName apiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceApiUri.From(apiName, workspaceName, serviceUri) };
}

public sealed record WorkspaceApiReleaseUri : ResourceUri
{
    public required WorkspaceApiReleasesUri Parent { get; init; }

    public required WorkspaceApiReleaseName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceApiReleaseUri From(WorkspaceApiReleaseName name, ApiName apiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceApiReleasesUri.From(apiName, workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceApiReleasesDirectory : ResourceDirectory
{
    public required WorkspaceApiDirectory Parent { get; init; }

    private static string Name { get; } = "releases";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceApiReleasesDirectory From(ApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceApiDirectory.From(apiName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceApiReleasesDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceApiReleasesDirectory { Parent = parent }
            : Option<WorkspaceApiReleasesDirectory>.None;
}

public sealed record WorkspaceApiReleaseDirectory : ResourceDirectory
{
    public required WorkspaceApiReleasesDirectory Parent { get; init; }

    public required WorkspaceApiReleaseName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceApiReleaseDirectory From(WorkspaceApiReleaseName name, ApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiReleasesDirectory.From(apiName, workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceApiReleaseDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceApiReleasesDirectory.TryParse(directory?.Parent, serviceDirectory)
        let name = WorkspaceApiReleaseName.From(directory!.Name)
        select new WorkspaceApiReleaseDirectory
        {
            Parent = parent,
            Name = name
        };
}

public sealed record WorkspaceApiReleaseInformationFile : ResourceFile
{
    public required WorkspaceApiReleaseDirectory Parent { get; init; }

    private static string Name { get; } = "releaseInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceApiReleaseInformationFile From(WorkspaceApiReleaseName name, ApiName apiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiReleaseDirectory.From(name, apiName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceApiReleaseInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceApiReleaseDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceApiReleaseInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceApiReleaseInformationFile>.None;
}

public sealed record WorkspaceApiReleaseDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiReleaseContract Properties { get; init; }

    public record ApiReleaseContract
    {
        [JsonPropertyName("apiId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiId { get; init; }

        [JsonPropertyName("notes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Notes { get; init; }
    }
}

public static class WorkspaceApiReleaseModule
{
    public static async ValueTask DeleteAll(this WorkspaceApiReleasesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var resourceUri = new WorkspaceApiReleaseUri { Parent = uri, Name = name };
                     await resourceUri.Delete(pipeline, cancellationToken);
                 }, cancellationToken);

    public static IAsyncEnumerable<WorkspaceApiReleaseName> ListNames(this WorkspaceApiReleasesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiReleaseName.From);

    public static IAsyncEnumerable<(WorkspaceApiReleaseName Name, WorkspaceApiReleaseDto Dto)> List(this WorkspaceApiReleasesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceApiReleaseUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceApiReleaseDto> GetDto(this WorkspaceApiReleaseUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiReleaseDto>();
    }

    public static async ValueTask Delete(this WorkspaceApiReleaseUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceApiReleaseUri uri, WorkspaceApiReleaseDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceApiReleaseDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceApiDirectory in WorkspaceApiModule.ListDirectories(serviceDirectory)
        let workspaceApiReleasesDirectory = new WorkspaceApiReleasesDirectory { Parent = workspaceApiDirectory }
        where workspaceApiReleasesDirectory.ToDirectoryInfo().Exists()
        from workspaceApiReleaseDirectoryInfo in workspaceApiReleasesDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceApiReleaseName.From(workspaceApiReleaseDirectoryInfo.Name)
        select new WorkspaceApiReleaseDirectory
        {
            Parent = workspaceApiReleasesDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceApiReleaseInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceApiReleaseDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceApiReleaseInformationFile { Parent = workspaceApiReleaseDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceApiReleaseInformationFile file, WorkspaceApiReleaseDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceApiReleaseDto> ReadDto(this WorkspaceApiReleaseInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiReleaseDto>();
    }
}
