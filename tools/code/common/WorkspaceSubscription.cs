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

public sealed record WorkspaceSubscriptionName : ResourceName, IResourceName<WorkspaceSubscriptionName>
{
    private WorkspaceSubscriptionName(string value) : base(value) { }

    public static WorkspaceSubscriptionName From(string value) => new(value);

    public static WorkspaceSubscriptionName Master { get; } = new("master");
}

public sealed record WorkspaceSubscriptionsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "subscriptions";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceSubscriptionsUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceSubscriptionUri : ResourceUri
{
    public required WorkspaceSubscriptionsUri Parent { get; init; }

    public required WorkspaceSubscriptionName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceSubscriptionUri From(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceSubscriptionsUri.From(workspaceName, serviceUri),
            Name = workspaceSubscriptionName
        };
}

public sealed record WorkspaceSubscriptionsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "subscriptions";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceSubscriptionsDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceSubscriptionsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceSubscriptionsDirectory { Parent = parent }
            : Option<WorkspaceSubscriptionsDirectory>.None;
}

public sealed record WorkspaceSubscriptionDirectory : ResourceDirectory
{
    public required WorkspaceSubscriptionsDirectory Parent { get; init; }

    public required WorkspaceSubscriptionName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceSubscriptionDirectory From(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceSubscriptionsDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceSubscriptionName
        };

    public static Option<WorkspaceSubscriptionDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceSubscriptionDirectory>.None
            : from parent in WorkspaceSubscriptionsDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceSubscriptionName.From(directory.Name)
              select new WorkspaceSubscriptionDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceSubscriptionInformationFile : ResourceFile
{
    public required WorkspaceSubscriptionDirectory Parent { get; init; }

    private static string Name { get; } = "subscriptionInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceSubscriptionInformationFile From(WorkspaceSubscriptionName workspaceSubscriptionName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceSubscriptionDirectory.From(workspaceSubscriptionName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceSubscriptionInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceSubscriptionDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceSubscriptionInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceSubscriptionInformationFile>.None;
}

public sealed record WorkspaceSubscriptionDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required SubscriptionContract Properties { get; init; }

    public sealed record SubscriptionContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("scope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Scope { get; init; }

        [JsonPropertyName("allowTracing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? AllowTracing { get; init; }

        [JsonPropertyName("ownerId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? OwnerId { get; init; }

        [JsonPropertyName("primaryKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? PrimaryKey { get; init; }

        [JsonPropertyName("secondaryKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SecondaryKey { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }
    }
}

public static class WorkspaceSubscriptionModule
{
    public static IAsyncEnumerable<WorkspaceSubscriptionName> ListNames(this WorkspaceSubscriptionsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceSubscriptionName.From);

    public static IAsyncEnumerable<(WorkspaceSubscriptionName Name, WorkspaceSubscriptionDto Dto)> List(this WorkspaceSubscriptionsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceSubscriptionUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceSubscriptionDto> GetDto(this WorkspaceSubscriptionUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceSubscriptionDto>();
    }

    public static async ValueTask Delete(this WorkspaceSubscriptionUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceSubscriptionUri uri, WorkspaceSubscriptionDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceSubscriptionDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceSubscriptionsDirectory = new WorkspaceSubscriptionsDirectory { Parent = workspaceDirectory }
        where workspaceSubscriptionsDirectory.ToDirectoryInfo().Exists()
        from workspaceSubscriptionDirectoryInfo in workspaceSubscriptionsDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceSubscriptionName.From(workspaceSubscriptionDirectoryInfo.Name)
        select new WorkspaceSubscriptionDirectory
        {
            Parent = workspaceSubscriptionsDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceSubscriptionInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceSubscriptionDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceSubscriptionInformationFile { Parent = workspaceSubscriptionDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

    public static async ValueTask WriteDto(this WorkspaceSubscriptionInformationFile file, WorkspaceSubscriptionDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceSubscriptionDto> ReadDto(this WorkspaceSubscriptionInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceSubscriptionDto>();
    }

    public static Option<WorkspaceProductName> TryGetProductName(WorkspaceSubscriptionDto dto) =>
        from scope in Prelude.Optional(dto.Properties.Scope)
        where scope.Contains("/products/", StringComparison.OrdinalIgnoreCase)
        from productNameString in scope.Split('/').LastOrNone()
        select WorkspaceProductName.From(productNameString);
}