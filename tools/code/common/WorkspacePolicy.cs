using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspacePolicyName : ResourceName, IResourceName<WorkspacePolicyName>
{
    private WorkspacePolicyName(string value) : base(value) { }

    public static WorkspacePolicyName From(string value) => new(value);
}

public sealed record WorkspacePoliciesUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspacePoliciesUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspacePolicyUri : ResourceUri
{
    public required WorkspacePoliciesUri Parent { get; init; }

    public required WorkspacePolicyName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspacePolicyUri From(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspacePoliciesUri.From(workspaceName, serviceUri),
            Name = workspacePolicyName
        };
}

public sealed record WorkspacePolicyFile : ResourceFile
{
    public required WorkspaceDirectory Parent { get; init; }

    public required WorkspacePolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static WorkspacePolicyFile From(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory),
            Name = workspacePolicyName
        };

    public static Option<WorkspacePolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseName(file)
        from parent in WorkspaceDirectory.TryParse(file?.Directory, serviceDirectory)
        select new WorkspacePolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<WorkspacePolicyName> TryParseName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => WorkspacePolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<WorkspacePolicyName>.None
        };
}

public sealed record WorkspacePolicyDto
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

public static class WorkspacePolicyModule
{
    public static async IAsyncEnumerable<WorkspacePolicyName> ListNames(this WorkspacePoliciesUri uri, HttpPipeline pipeline, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The REST API call to list policy names returns incorrectly formatted names.
        // For now, we'll return the single policy named "policy" if it exists
        var policyName = WorkspacePolicyName.From("policy");
        var policyUri = uri.ToUri().AppendPathSegment(policyName.Value).ToUri();

        var option = await pipeline.GetJsonObjectOption(policyUri, cancellationToken);

        if (option.IsSome)
        {
            yield return policyName;
        }
    }

    public static IAsyncEnumerable<(WorkspacePolicyName Name, WorkspacePolicyDto Dto)> List(this WorkspacePoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspacePolicyUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
               return (name, dto);
           });

    public static async ValueTask<WorkspacePolicyDto> GetDto(this WorkspacePolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<WorkspacePolicyDto>();
    }

    public static async ValueTask Delete(this WorkspacePolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspacePolicyUri uri, WorkspacePolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<WorkspacePolicyFile> ListPolicyFiles(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory)
    {
        var parentDirectory = WorkspaceDirectory.From(workspaceName, serviceDirectory);

        return parentDirectory.ToDirectoryInfo()
                              .ListFiles("*")
                              .Choose(WorkspacePolicyFile.TryParseName)
                              .Select(name => new WorkspacePolicyFile { Name = name, Parent = parentDirectory });
    }

    public static async ValueTask WritePolicy(this WorkspacePolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this WorkspacePolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}