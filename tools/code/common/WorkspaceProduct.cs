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

public sealed record WorkspaceProductsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "products";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceProductsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceProductUri : ResourceUri
{
    public required WorkspaceProductsUri Parent { get; init; }
    public required ProductName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceProductUri From(ProductName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceProductsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceProductsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "products";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceProductsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceProductsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceProductsDirectory { Parent = parent }
            : Option<WorkspaceProductsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceProductDirectory : ResourceDirectory
{
    public required WorkspaceProductsDirectory Parent { get; init; }

    public required ProductName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceProductDirectory From(ProductName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceProductDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceProductsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceProductDirectory
        {
            Parent = parent,
            Name = ProductName.From(directory!.Name)
        };
}

public sealed record WorkspaceProductInformationFile : ResourceFile
{
    public required WorkspaceProductDirectory Parent { get; init; }

    private static string Name { get; } = "productInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceProductInformationFile From(ProductName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceProductInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceProductDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceProductInformationFile { Parent = parent }
            : Option<WorkspaceProductInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
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
    public static IAsyncEnumerable<ProductName> ListNames(this WorkspaceProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ProductName.From);

    public static IAsyncEnumerable<(ProductName Name, WorkspaceProductDto Dto)> List(this WorkspaceProductsUri workspaceProductsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceProductsUri.ListNames(pipeline, cancellationToken)
                            .SelectAwait(async name =>
                            {
                                var uri = new WorkspaceProductUri { Parent = workspaceProductsUri, Name = name };
                                var dto = await uri.GetDto(pipeline, cancellationToken);
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