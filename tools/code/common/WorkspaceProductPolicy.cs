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

public sealed record WorkspaceProductPolicyName : ResourceName, IResourceName<WorkspaceProductPolicyName>
{
    private WorkspaceProductPolicyName(string value) : base(value) { }

    public static WorkspaceProductPolicyName From(string value) => new(value);
}

public sealed record WorkspaceProductPoliciesUri : ResourceUri
{
    public required WorkspaceProductUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceProductPoliciesUri From(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceProductUri.From(workspaceProductName, workspaceName, serviceUri) };
}

public sealed record WorkspaceProductPolicyUri : ResourceUri
{
    public required WorkspaceProductPoliciesUri Parent { get; init; }

    public required WorkspaceProductPolicyName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceProductPolicyUri From(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceProductPoliciesUri.From(workspaceProductName, workspaceName, serviceUri),
            Name = workspaceProductPolicyName
        };
}

public sealed record WorkspaceProductPolicyFile : ResourceFile
{
    public required WorkspaceProductDirectory Parent { get; init; }

    public required WorkspaceProductPolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static WorkspaceProductPolicyFile From(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceProductDirectory.From(workspaceProductName, workspaceName, serviceDirectory),
            Name = workspaceProductPolicyName
        };

    public static Option<WorkspaceProductPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseName(file)
        from parent in WorkspaceProductDirectory.TryParse(file?.Directory, serviceDirectory)
        select new WorkspaceProductPolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<WorkspaceProductPolicyName> TryParseName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => WorkspaceProductPolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<WorkspaceProductPolicyName>.None
        };
}

public sealed record WorkspaceProductPolicyDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required PolicyContract Properties { get; init; }

    public sealed record PolicyContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }
}

public static class WorkspaceProductPolicyModule
{
    public static IAsyncEnumerable<WorkspaceProductPolicyName> ListNames(this WorkspaceProductPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceProductPolicyName.From);

    public static IAsyncEnumerable<(WorkspaceProductPolicyName Name, WorkspaceProductPolicyDto Dto)> List(this WorkspaceProductPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceProductPolicyUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceProductPolicyDto> GetDto(this WorkspaceProductPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<WorkspaceProductPolicyDto>();
    }

    public static async ValueTask Delete(this WorkspaceProductPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceProductPolicyUri uri, WorkspaceProductPolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceProductPolicyFile> ListPolicyFiles(WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory)
    {
        var parentDirectory = WorkspaceProductDirectory.From(workspaceProductName, workspaceName, serviceDirectory);

        return parentDirectory.ToDirectoryInfo()
                              .ListFiles("*")
                              .Choose(WorkspaceProductPolicyFile.TryParseName)
                              .Select(name => new WorkspaceProductPolicyFile { Name = name, Parent = parentDirectory });
    }

    public static async ValueTask WritePolicy(this WorkspaceProductPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this WorkspaceProductPolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}