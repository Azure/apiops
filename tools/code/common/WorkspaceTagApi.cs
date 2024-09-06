using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceTagApisUri : ResourceUri
{
    public required WorkspaceTagUri Parent { get; init; }

    private static string PathSegment { get; } = "apiLinks";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceTagApisUri From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceTagUri.From(workspaceTagName, workspaceName, serviceUri) };
}

public sealed record WorkspaceTagApiUri : ResourceUri
{
    public required WorkspaceTagApisUri Parent { get; init; }

    public required WorkspaceApiName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceTagApiUri From(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceTagApisUri.From(workspaceTagName, workspaceName, serviceUri),
            Name = workspaceApiName
        };
}

public sealed record WorkspaceTagApisDirectory : ResourceDirectory
{
    public required WorkspaceTagDirectory Parent { get; init; }

    private static string Name { get; } = "apis";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceTagApisDirectory From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceTagDirectory.From(workspaceTagName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceTagApisDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceTagDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceTagApisDirectory { Parent = parent }
            : Option<WorkspaceTagApisDirectory>.None;
}

public sealed record WorkspaceTagApiDirectory : ResourceDirectory
{
    public required WorkspaceTagApisDirectory Parent { get; init; }

    public required WorkspaceApiName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceTagApiDirectory From(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagApisDirectory.From(workspaceTagName, workspaceName, serviceDirectory),
            Name = workspaceApiName
        };

    public static Option<WorkspaceTagApiDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceTagApiDirectory>.None
            : from parent in WorkspaceTagApisDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceApiName.From(directory.Name)
              select new WorkspaceTagApiDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceTagApiInformationFile : ResourceFile
{
    public required WorkspaceTagApiDirectory Parent { get; init; }

    private static string Name { get; } = "apiInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceTagApiInformationFile From(WorkspaceApiName workspaceApiName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagApiDirectory.From(workspaceApiName, workspaceTagName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceTagApiInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceTagApiDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceTagApiInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceTagApiInformationFile>.None;
}

public sealed record WorkspaceTagApiDto
{
    public static WorkspaceTagApiDto Instance { get; } = new();
}

public static class WorkspaceTagApiModule
{
    public static IAsyncEnumerable<WorkspaceApiName> ListNames(this WorkspaceTagApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiName.From);

    public static IAsyncEnumerable<(WorkspaceApiName Name, WorkspaceTagApiDto Dto)> List(this WorkspaceTagApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceTagApiUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceTagApiDto> GetDto(this WorkspaceTagApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceTagApiDto>();
    }

    public static async ValueTask Delete(this WorkspaceTagApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceTagApiUri uri, WorkspaceTagApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceTagApiDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceTagDirectory in WorkspaceTagModule.ListDirectories(serviceDirectory)
        let workspaceTagApisDirectory = new WorkspaceTagApisDirectory { Parent = workspaceTagDirectory }
        where workspaceTagApisDirectory.ToDirectoryInfo().Exists()
        from workspaceTagApiDirectoryInfo in workspaceTagApisDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceApiName.From(workspaceTagApiDirectoryInfo.Name)
        select new WorkspaceTagApiDirectory
        {
            Parent = workspaceTagApisDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceTagApiInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceTagApiDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceTagApiInformationFile { Parent = workspaceTagApiDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceTagApiInformationFile file, WorkspaceTagApiDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceTagApiDto> ReadDto(this WorkspaceTagApiInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceTagApiDto>();
    }
}