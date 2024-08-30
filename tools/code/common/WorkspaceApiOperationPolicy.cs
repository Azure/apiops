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

public sealed record WorkspaceApiOperationPolicyName : ResourceName, IResourceName<WorkspaceApiOperationPolicyName>
{
    private WorkspaceApiOperationPolicyName(string value) : base(value) { }

    public static WorkspaceApiOperationPolicyName From(string value) => new(value);
}

public sealed record WorkspaceApiOperationPoliciesUri : ResourceUri
{
    public required WorkspaceApiOperationUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceApiOperationPoliciesUri From(WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceApiOperationUri.From(workspaceApiOperationName, workspaceApiName, workspaceName, serviceUri) };
}

public sealed record WorkspaceApiOperationPolicyUri : ResourceUri
{
    public required WorkspaceApiOperationPoliciesUri Parent { get; init; }

    public required WorkspaceApiOperationPolicyName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceApiOperationPolicyUri From(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceApiOperationPoliciesUri.From(workspaceApiOperationName, workspaceApiName, workspaceName, serviceUri),
            Name = workspaceApiOperationPolicyName
        };
}

public sealed record WorkspaceApiOperationPolicyFile : ResourceFile
{
    public required WorkspaceApiOperationDirectory Parent { get; init; }

    public required WorkspaceApiOperationPolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static WorkspaceApiOperationPolicyFile From(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiOperationDirectory.From(workspaceApiOperationName, workspaceApiName, workspaceName, serviceDirectory),
            Name = workspaceApiOperationPolicyName
        };

    public static Option<WorkspaceApiOperationPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseName(file)
        from parent in WorkspaceApiOperationDirectory.TryParse(file?.Directory, serviceDirectory)
        select new WorkspaceApiOperationPolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<WorkspaceApiOperationPolicyName> TryParseName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => WorkspaceApiOperationPolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<WorkspaceApiOperationPolicyName>.None
        };
}

public sealed record WorkspaceApiOperationPolicyDto
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

public static class WorkspaceApiOperationPolicyModule
{
    public static IAsyncEnumerable<WorkspaceApiOperationPolicyName> ListNames(this WorkspaceApiOperationPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiOperationPolicyName.From);

    public static IAsyncEnumerable<(WorkspaceApiOperationPolicyName Name, WorkspaceApiOperationPolicyDto Dto)> List(this WorkspaceApiOperationPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceApiOperationPolicyUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceApiOperationPolicyDto> GetDto(this WorkspaceApiOperationPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiOperationPolicyDto>();
    }

    public static async ValueTask Delete(this WorkspaceApiOperationPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceApiOperationPolicyUri uri, WorkspaceApiOperationPolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceApiOperationPolicyFile> ListPolicyFiles(WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory)
    {
        var parentDirectory = WorkspaceApiOperationDirectory.From(workspaceApiOperationName, workspaceApiName, workspaceName, serviceDirectory);

        return parentDirectory.ToDirectoryInfo()
                              .ListFiles("*")
                              .Choose(WorkspaceApiOperationPolicyFile.TryParseName)
                              .Select(name => new WorkspaceApiOperationPolicyFile { Name = name, Parent = parentDirectory });
    }

    public static async ValueTask WritePolicy(this WorkspaceApiOperationPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this WorkspaceApiOperationPolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}