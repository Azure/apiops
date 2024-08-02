using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceName : ResourceName, IResourceName<WorkspaceName>
{
    private WorkspaceName(string value) : base(value) { }

    public static WorkspaceName From(string value) => new(value);
}

public sealed record WorkspacesUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "workspaces";

    protected override Uri Value =>
        ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspacesUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record WorkspaceUri : ResourceUri
{
    public required WorkspacesUri Parent { get; init; }

    public required WorkspaceName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspacesUri.From(serviceUri),
            Name = name
        };
}

public sealed record WorkspacesDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "workspaces";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspacesDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<WorkspacesDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new WorkspacesDirectory { ServiceDirectory = serviceDirectory }
            : Option<WorkspacesDirectory>.None;
}

public sealed record WorkspaceDirectory : ResourceDirectory
{
    public required WorkspacesDirectory Parent { get; init; }

    public required WorkspaceName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspacesDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null
            ? from parent in WorkspacesDirectory.TryParse(directory?.Parent, serviceDirectory)
              let name = WorkspaceName.From(directory!.Name)
              select new WorkspaceDirectory
              {
                  Parent = parent,
                  Name = name
              }
            : Option<WorkspaceDirectory>.None;
}

public sealed record WorkspaceInformationFile : ResourceFile
{
    public required WorkspaceDirectory Parent { get; init; }

    public static string Name { get; } = "workspaceInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceInformationFile From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceDirectory.From(name, serviceDirectory)
        };

    public static Option<WorkspaceInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null &&
        file.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceInformationFile>.None;
}

public sealed record WorkspaceDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required WorkspaceContract Properties { get; init; }

    public sealed record WorkspaceContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }
    }
}

public static class WorkspaceModule
{
    public static async ValueTask DeleteAll(this WorkspacesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name =>
                 {
                     var resourceUri = new WorkspaceUri { Parent = uri, Name = name };
                     await resourceUri.Delete(pipeline, cancellationToken);
                 }, cancellationToken);

    public static IAsyncEnumerable<WorkspaceName> ListNames(this WorkspacesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var exceptionHandler = (HttpRequestException exception) =>
            exception.StatusCode == HttpStatusCode.BadRequest
             && exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase)
            ? AsyncEnumerable.Empty<WorkspaceName>()
            : throw exception;

        return pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                       .Select(jsonObject => jsonObject.GetStringProperty("name"))
                       .Select(WorkspaceName.From)
                       .Catch(exceptionHandler);
    }

    public static IAsyncEnumerable<(WorkspaceName Name, WorkspaceDto Dto)> List(this WorkspacesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<Option<WorkspaceDto>> TryGetDto(this WorkspaceUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<WorkspaceDto>());
    }

    public static async ValueTask<WorkspaceDto> GetDto(this WorkspaceUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceDto>();
    }

    public static async ValueTask Delete(this WorkspaceUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceUri uri, WorkspaceDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var workspacesDirectory = WorkspacesDirectory.From(serviceDirectory);

        return from workspacesDirectoryInfo in workspacesDirectory.ToDirectoryInfo().ListDirectories("*")
               let name = WorkspaceName.From(workspacesDirectoryInfo.Name)
               select new WorkspaceDirectory
               {
                   Parent = workspacesDirectory,
                   Name = name
               };
    }

    public static IEnumerable<WorkspaceInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceInformationFile { Parent = workspaceDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceInformationFile file, WorkspaceDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceDto> ReadDto(this WorkspaceInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceDto>();
    }
}