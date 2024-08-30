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

public sealed record WorkspaceProductName : ResourceName, IResourceName<WorkspaceProductName>
{
    private WorkspaceProductName(string value) : base(value) { }

    public static WorkspaceProductName From(string value) => new(value);
}

public sealed record WorkspaceProductsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "products";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceProductsUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceProductUri : ResourceUri
{
    public required WorkspaceProductsUri Parent { get; init; }

    public required WorkspaceProductName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceProductUri From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceProductsUri.From(workspaceName, serviceUri),
            Name = workspaceProductName
        };
}

public sealed record WorkspaceProductsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "products";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceProductsDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceProductsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceProductsDirectory { Parent = parent }
            : Option<WorkspaceProductsDirectory>.None;
}

public sealed record WorkspaceProductDirectory : ResourceDirectory
{
    public required WorkspaceProductsDirectory Parent { get; init; }

    public required WorkspaceProductName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceProductDirectory From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductsDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceProductName
        };

    public static Option<WorkspaceProductDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceProductDirectory>.None
            : from parent in WorkspaceProductsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceProductName.From(directory.Name)
              select new WorkspaceProductDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceProductInformationFile : ResourceFile
{
    public required WorkspaceProductDirectory Parent { get; init; }

    private static string Name { get; } = "productInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceProductInformationFile From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductDirectory.From(workspaceProductName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceProductInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceProductDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceProductInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceProductInformationFile>.None;
}

public sealed record WorkspaceProductDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ProductContract Properties { get; init; }

    public record ProductContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("approvalRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ApprovalRequired { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("subscriptionsLimit")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? SubscriptionsLimit { get; init; }

        [JsonPropertyName("terms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Terms { get; init; }
    }
}

public static class WorkspaceProductModule
{
    public static IAsyncEnumerable<WorkspaceProductName> ListNames(this WorkspaceProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceProductName.From);

    public static IAsyncEnumerable<(WorkspaceProductName Name, WorkspaceProductDto Dto)> List(this WorkspaceProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceProductUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceProductDto> GetDto(this WorkspaceProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductDto>();
    }

    public static async ValueTask Delete(this WorkspaceProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceProductUri uri, WorkspaceProductDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceProductDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceProductsDirectory = new WorkspaceProductsDirectory { Parent = workspaceDirectory }
        where workspaceProductsDirectory.ToDirectoryInfo().Exists()
        from workspaceProductDirectoryInfo in workspaceProductsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceProductName.From(workspaceProductDirectoryInfo.Name)
        select new WorkspaceProductDirectory
        {
            Parent = workspaceProductsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceProductInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceProductDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceProductInformationFile { Parent = workspaceProductDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceProductInformationFile file, WorkspaceProductDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceProductDto> ReadDto(this WorkspaceProductInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductDto>();
    }
}