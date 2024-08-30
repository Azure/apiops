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

public sealed record WorkspaceApiPolicyName : ResourceName, IResourceName<WorkspaceApiPolicyName>
{
    private WorkspaceApiPolicyName(string value) : base(value) { }

    public static WorkspaceApiPolicyName From(string value) => new(value);
}

public sealed record WorkspaceApiPoliciesUri : ResourceUri
{
    public required WorkspaceApiUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceApiPoliciesUri From(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceApiUri.From(workspaceApiName, workspaceName, serviceUri) };
}

public sealed record WorkspaceApiPolicyUri : ResourceUri
{
    public required WorkspaceApiPoliciesUri Parent { get; init; }

    public required WorkspaceApiPolicyName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceApiPolicyUri From(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceApiPoliciesUri.From(workspaceApiName, workspaceName, serviceUri),
            Name = workspaceApiPolicyName
        };
}

public sealed record WorkspaceApiPolicyFile : ResourceFile
{
    public required WorkspaceApiDirectory Parent { get; init; }

    public required WorkspaceApiPolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static WorkspaceApiPolicyFile From(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceApiDirectory.From(workspaceApiName, workspaceName, serviceDirectory),
            Name = workspaceApiPolicyName
        };

    public static Option<WorkspaceApiPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseName(file)
        from parent in WorkspaceApiDirectory.TryParse(file?.Directory, serviceDirectory)
        select new WorkspaceApiPolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<WorkspaceApiPolicyName> TryParseName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => WorkspaceApiPolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<WorkspaceApiPolicyName>.None
        };
}

public sealed record WorkspaceApiPolicyDto
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

public static class WorkspaceApiPolicyModule
{
    public static IAsyncEnumerable<WorkspaceApiPolicyName> ListNames(this WorkspaceApiPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceApiPolicyName.From);

    public static IAsyncEnumerable<(WorkspaceApiPolicyName Name, WorkspaceApiPolicyDto Dto)> List(this WorkspaceApiPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceApiPolicyUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspaceApiPolicyDto> GetDto(this WorkspaceApiPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<WorkspaceApiPolicyDto>();
    }

    public static async ValueTask Delete(this WorkspaceApiPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceApiPolicyUri uri, WorkspaceApiPolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspaceApiPolicyFile> ListPolicyFiles(WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory)
    {
        var parentDirectory = WorkspaceApiDirectory.From(workspaceApiName, workspaceName, serviceDirectory);

        return parentDirectory.ToDirectoryInfo()
                              .ListFiles("*")
                              .Choose(WorkspaceApiPolicyFile.TryParseName)
                              .Select(name => new WorkspaceApiPolicyFile { Name = name, Parent = parentDirectory });
    }

    public static async ValueTask WritePolicy(this WorkspaceApiPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this WorkspaceApiPolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}