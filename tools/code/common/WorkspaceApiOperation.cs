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

public sealed record WorkspaceApiOperationName : ResourceName, IResourceName<WorkspaceApiOperationName>
{
    private WorkspaceApiOperationName(string value) : base(value) { }

    public static WorkspaceApiOperationName From(string value) => new(value);
}

public sealed record WorkspaceApiOperationsUri : ResourceUri
{
    public required WorkspaceApiUri Parent { get; init; }

    private static string PathSegment { get; } = "operations";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceApiOperationsUri From(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceApiUri.From(workspaceApiName, workspaceName, serviceUri) };
}

public sealed record WorkspaceApiOperationUri : ResourceUri
{
    public required WorkspaceApiOperationsUri Parent { get; init; }

    public required WorkspaceApiOperationName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceApiOperationUri From(WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceApiOperationsUri.From(workspaceApiName, workspaceName, serviceUri),
            Name = workspaceApiOperationName
        };
}

public sealed record WorkspaceApiOperationsDirectory : ResourceDirectory
{
    public required WorkspaceApiDirectory Parent { get; init; }

    private static string Name { get; } = "operations";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceApiOperationsDirectory From(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceApiDirectory.From(workspaceApiName, workspaceName, serviceDirectory) };

    public static Option<WorkspaceApiOperationsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceApiOperationsDirectory { Parent = parent }
            : Option<WorkspaceApiOperationsDirectory>.None;
}

public sealed record WorkspaceApiOperationDirectory : ResourceDirectory
{
    public required WorkspaceApiOperationsDirectory Parent { get; init; }

    public required WorkspaceApiOperationName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceApiOperationDirectory From(WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiOperationsDirectory.From(workspaceApiName, workspaceName, serviceDirectory),
            Name = workspaceApiOperationName
        };

    public static Option<WorkspaceApiOperationDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceApiOperationDirectory>.None
            : from parent in WorkspaceApiOperationsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceApiOperationName.From(directory.Name)
              select new WorkspaceApiOperationDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceApiOperationDto
{
}

public static class WorkspaceApiOperationModule
{
    public static IAsyncEnumerable<WorkspaceApiOperationName> ListNames(this WorkspaceApiOperationsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiOperationName.From);

    public static IAsyncEnumerable<(WorkspaceApiOperationName Name, WorkspaceApiOperationDto Dto)> List(this WorkspaceApiOperationsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceApiOperationUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceApiOperationDto> GetDto(this WorkspaceApiOperationUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiOperationDto>();
    }

    public static async ValueTask Delete(this WorkspaceApiOperationUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceApiOperationUri uri, WorkspaceApiOperationDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceApiOperationDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceApiDirectory in WorkspaceApiModule.ListDirectories(serviceDirectory)
        let workspaceApiOperationsDirectory = new WorkspaceApiOperationsDirectory { Parent = workspaceApiDirectory }
        where workspaceApiOperationsDirectory.ToDirectoryInfo().Exists()
        from workspaceApiOperationDirectoryInfo in workspaceApiOperationsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceApiOperationName.From(workspaceApiOperationDirectoryInfo.Name)
        select new WorkspaceApiOperationDirectory
        {
            Parent = workspaceApiOperationsDirectory,
            Name = name
        };

}