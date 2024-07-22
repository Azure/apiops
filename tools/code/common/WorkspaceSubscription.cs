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

public sealed record WorkspaceSubscriptionsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "subscriptions";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceSubscriptionsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceSubscriptionUri : ResourceUri
{
    public required WorkspaceSubscriptionsUri Parent { get; init; }
    public required SubscriptionName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceSubscriptionUri From(SubscriptionName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceSubscriptionsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceSubscriptionsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "subscriptions";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceSubscriptionsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceSubscriptionsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceSubscriptionsDirectory { Parent = parent }
            : Option<WorkspaceSubscriptionsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceSubscriptionDirectory : ResourceDirectory
{
    public required WorkspaceSubscriptionsDirectory Parent { get; init; }

    public required SubscriptionName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceSubscriptionDirectory From(SubscriptionName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceSubscriptionsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceSubscriptionDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceSubscriptionsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceSubscriptionDirectory
        {
            Parent = parent,
            Name = SubscriptionName.From(directory!.Name)
        };
}

public sealed record WorkspaceSubscriptionInformationFile : ResourceFile
{
    public required WorkspaceSubscriptionDirectory Parent { get; init; }

    private static string Name { get; } = "subscriptionInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceSubscriptionInformationFile From(SubscriptionName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceSubscriptionDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceSubscriptionInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceSubscriptionDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceSubscriptionInformationFile { Parent = parent }
            : Option<WorkspaceSubscriptionInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
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
    public static IAsyncEnumerable<SubscriptionName> ListNames(this WorkspaceSubscriptionsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(SubscriptionName.From);

    public static IAsyncEnumerable<(SubscriptionName Name, WorkspaceSubscriptionDto Dto)> List(this WorkspaceSubscriptionsUri workspaceSubscriptionsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceSubscriptionsUri.ListNames(pipeline, cancellationToken)
                                 .SelectAwait(async name =>
                                 {
                                     var uri = new WorkspaceSubscriptionUri { Parent = workspaceSubscriptionsUri, Name = name };
                                     var dto = await uri.GetDto(pipeline, cancellationToken);
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
}