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

public sealed record WorkspaceProductApisUri : ResourceUri
{
    public required WorkspaceProductUri Parent { get; init; }

    private static string PathSegment { get; } = "apiLinks";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceProductApisUri From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceProductUri.From(workspaceProductName, workspaceName, serviceUri) };
}

public sealed record WorkspaceProductApiUri : ResourceUri
{
    public required WorkspaceProductApisUri Parent { get; init; }

    public required WorkspaceApiName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceProductApiUri From(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceProductApisUri.From(workspaceProductName, workspaceName, serviceUri),
            Name = workspaceApiName
        };
}

public sealed record WorkspaceProductApisDirectory : ResourceDirectory
{
    public required WorkspaceProductDirectory Parent { get; init; }

    private static string Name { get; } = "apis";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceProductApisDirectory From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceProductDirectory.From(workspaceProductName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceProductApisDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceProductDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceProductApisDirectory { Parent = parent }
            : Option<WorkspaceProductApisDirectory>.None;
}

public sealed record WorkspaceProductApiDirectory : ResourceDirectory
{
    public required WorkspaceProductApisDirectory Parent { get; init; }

    public required WorkspaceApiName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceProductApiDirectory From(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductApisDirectory.From(workspaceProductName, workspaceName, serviceDirectory),
            Name = workspaceApiName
        };

    public static Option<WorkspaceProductApiDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceProductApiDirectory>.None
            : from parent in WorkspaceProductApisDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceApiName.From(directory.Name)
              select new WorkspaceProductApiDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceProductApiInformationFile : ResourceFile
{
    public required WorkspaceProductApiDirectory Parent { get; init; }

    private static string Name { get; } = "apiInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceProductApiInformationFile From(WorkspaceApiName workspaceApiName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductApiDirectory.From(workspaceApiName, workspaceProductName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceProductApiInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceProductApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceProductApiInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceProductApiInformationFile>.None;
}

public sealed record WorkspaceProductApiDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ApiContract Properties { get; init; }

    public record ApiContract
    {
        [JsonPropertyName("apiId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? ApiId { get; init; }
    }
}

public static class WorkspaceProductApiModule
{
    public static IAsyncEnumerable<WorkspaceApiName> ListNames(this WorkspaceProductApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiName.From);

    public static IAsyncEnumerable<(WorkspaceApiName Name, WorkspaceProductApiDto Dto)> List(this WorkspaceProductApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceProductApiUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceProductApiDto> GetDto(this WorkspaceProductApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductApiDto>();
    }

    public static async ValueTask Delete(this WorkspaceProductApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceProductApiUri uri, WorkspaceProductApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceProductApiDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceProductDirectory in WorkspaceProductModule.ListDirectories(serviceDirectory)
        let workspaceProductApisDirectory = new WorkspaceProductApisDirectory { Parent = workspaceProductDirectory }
        where workspaceProductApisDirectory.ToDirectoryInfo().Exists()
        from workspaceProductApiDirectoryInfo in workspaceProductApisDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceApiName.From(workspaceProductApiDirectoryInfo.Name)
        select new WorkspaceProductApiDirectory
        {
            Parent = workspaceProductApisDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceProductApiInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceProductApiDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceProductApiInformationFile { Parent = workspaceProductApiDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceProductApiInformationFile file, WorkspaceProductApiDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceProductApiDto> ReadDto(this WorkspaceProductApiInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductApiDto>();
    }
}