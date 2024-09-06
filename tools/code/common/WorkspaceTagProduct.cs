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

public sealed record WorkspaceTagProductsUri : ResourceUri
{
    public required WorkspaceTagUri Parent { get; init; }

    private static string PathSegment { get; } = "productLinks";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceTagProductsUri From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceTagUri.From(workspaceTagName, workspaceName, serviceUri) };
}

public sealed record WorkspaceTagProductUri : ResourceUri
{
    public required WorkspaceTagProductsUri Parent { get; init; }

    public required WorkspaceProductName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceTagProductUri From(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceTagProductsUri.From(workspaceTagName, workspaceName, serviceUri),
            Name = workspaceProductName
        };
}

public sealed record WorkspaceTagProductsDirectory : ResourceDirectory
{
    public required WorkspaceTagDirectory Parent { get; init; }

    private static string Name { get; } = "products";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceTagProductsDirectory From(WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceTagDirectory.From(workspaceTagName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceTagProductsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceTagDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceTagProductsDirectory { Parent = parent }
            : Option<WorkspaceTagProductsDirectory>.None;
}

public sealed record WorkspaceTagProductDirectory : ResourceDirectory
{
    public required WorkspaceTagProductsDirectory Parent { get; init; }

    public required WorkspaceProductName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceTagProductDirectory From(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagProductsDirectory.From(workspaceTagName, workspaceName, serviceDirectory),
            Name = workspaceProductName
        };

    public static Option<WorkspaceTagProductDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceTagProductDirectory>.None
            : from parent in WorkspaceTagProductsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceProductName.From(directory.Name)
              select new WorkspaceTagProductDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceTagProductInformationFile : ResourceFile
{
    public required WorkspaceTagProductDirectory Parent { get; init; }

    private static string Name { get; } = "productInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceTagProductInformationFile From(WorkspaceProductName workspaceProductName, WorkspaceTagName workspaceTagName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceTagProductDirectory.From(workspaceProductName, workspaceTagName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceTagProductInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceTagProductDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceTagProductInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceTagProductInformationFile>.None;
}

public sealed record WorkspaceTagProductDto
{
    public static WorkspaceTagProductDto Instance { get; } = new();
}

public static class WorkspaceTagProductModule
{
    public static IAsyncEnumerable<WorkspaceProductName> ListNames(this WorkspaceTagProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceProductName.From);

    public static IAsyncEnumerable<(WorkspaceProductName Name, WorkspaceTagProductDto Dto)> List(this WorkspaceTagProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceTagProductUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceTagProductDto> GetDto(this WorkspaceTagProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceTagProductDto>();
    }

    public static async ValueTask Delete(this WorkspaceTagProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceTagProductUri uri, WorkspaceTagProductDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceTagProductDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceTagDirectory in WorkspaceTagModule.ListDirectories(serviceDirectory)
        let workspaceTagProductsDirectory = new WorkspaceTagProductsDirectory { Parent = workspaceTagDirectory }
        where workspaceTagProductsDirectory.ToDirectoryInfo().Exists()
        from workspaceTagProductDirectoryInfo in workspaceTagProductsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceProductName.From(workspaceTagProductDirectoryInfo.Name)
        select new WorkspaceTagProductDirectory
        {
            Parent = workspaceTagProductsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceTagProductInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceTagProductDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceTagProductInformationFile { Parent = workspaceTagProductDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceTagProductInformationFile file, WorkspaceTagProductDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceTagProductDto> ReadDto(this WorkspaceTagProductInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceTagProductDto>();
    }
}