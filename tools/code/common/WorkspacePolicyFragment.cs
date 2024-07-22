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

public sealed record WorkspacePolicyFragmentsUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "policyFragments";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspacePolicyFragmentsUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspacePolicyFragmentUri : ResourceUri
{
    public required WorkspacePolicyFragmentsUri Parent { get; init; }
    public required PolicyFragmentName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspacePolicyFragmentUri From(PolicyFragmentName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspacePolicyFragmentsUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspacePolicyFragmentsDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "policy fragments";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspacePolicyFragmentsDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspacePolicyFragmentsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspacePolicyFragmentsDirectory { Parent = parent }
            : Option<WorkspacePolicyFragmentsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspacePolicyFragmentDirectory : ResourceDirectory
{
    public required WorkspacePolicyFragmentsDirectory Parent { get; init; }

    public required PolicyFragmentName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspacePolicyFragmentDirectory From(PolicyFragmentName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspacePolicyFragmentsDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspacePolicyFragmentDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspacePolicyFragmentsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspacePolicyFragmentDirectory
        {
            Parent = parent,
            Name = PolicyFragmentName.From(directory!.Name)
        };
}

public sealed record WorkspacePolicyFragmentInformationFile : ResourceFile
{
    public required WorkspacePolicyFragmentDirectory Parent { get; init; }

    private static string Name { get; } = "policyFragmentInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspacePolicyFragmentInformationFile From(PolicyFragmentName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspacePolicyFragmentDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspacePolicyFragmentInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspacePolicyFragmentDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspacePolicyFragmentInformationFile { Parent = parent }
            : Option<WorkspacePolicyFragmentInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record WorkspacePolicyFragmentPolicyFile : ResourceFile
{
    public required WorkspacePolicyFragmentDirectory Parent { get; init; }
    private static string Name { get; } = "policy.xml";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspacePolicyFragmentPolicyFile From(PolicyFragmentName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new WorkspacePolicyFragmentDirectory
            {
                Parent = WorkspacePolicyFragmentsDirectory.From(workspaceName, serviceDirectory),
                Name = name
            }
        };

    public static Option<WorkspacePolicyFragmentPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in WorkspacePolicyFragmentDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspacePolicyFragmentPolicyFile { Parent = parent }
            : Option<WorkspacePolicyFragmentPolicyFile>.None;
}

public sealed record WorkspacePolicyFragmentDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required PolicyFragmentContract Properties { get; init; }

    public sealed record PolicyFragmentContract
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

public static class WorkspacePolicyFragmentModule
{
    public static IAsyncEnumerable<PolicyFragmentName> ListNames(this WorkspacePolicyFragmentsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(PolicyFragmentName.From);

    public static IAsyncEnumerable<(PolicyFragmentName Name, WorkspacePolicyFragmentDto Dto)> List(this WorkspacePolicyFragmentsUri workspacePolicyFragmentsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspacePolicyFragmentsUri.ListNames(pipeline, cancellationToken)
                                   .SelectAwait(async name =>
                                   {
                                       var uri = new WorkspacePolicyFragmentUri { Parent = workspacePolicyFragmentsUri, Name = name };
                                       var dto = await uri.GetDto(pipeline, cancellationToken);
                                       return (name, dto);
                                   });

    public static async ValueTask<WorkspacePolicyFragmentDto> GetDto(this WorkspacePolicyFragmentUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<WorkspacePolicyFragmentDto>();
    }

    public static async ValueTask Delete(this WorkspacePolicyFragmentUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspacePolicyFragmentUri uri, WorkspacePolicyFragmentDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static async ValueTask WriteDto(this WorkspacePolicyFragmentInformationFile file, WorkspacePolicyFragmentDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspacePolicyFragmentDto> ReadDto(this WorkspacePolicyFragmentInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspacePolicyFragmentDto>();
    }

    public static async ValueTask WritePolicy(this WorkspacePolicyFragmentPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }
}