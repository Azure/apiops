using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceNamedValuesUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "namedValues";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceNamedValuesUri From(WorkspaceName name, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(name, serviceUri) };
}

public sealed record WorkspaceNamedValueUri : ResourceUri
{
    public required WorkspaceNamedValuesUri Parent { get; init; }
    public required NamedValueName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static WorkspaceNamedValueUri From(NamedValueName name, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceNamedValuesUri.From(workspaceName, serviceUri),
            Name = name
        };
}

public sealed record WorkspaceNamedValuesDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }
    private static string Name { get; } = "named values";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceNamedValuesDirectory From(WorkspaceName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(name, serviceDirectory) };

    public static Option<WorkspaceNamedValuesDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceNamedValuesDirectory { Parent = parent }
            : Option<WorkspaceNamedValuesDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record WorkspaceNamedValueDirectory : ResourceDirectory
{
    public required WorkspaceNamedValuesDirectory Parent { get; init; }

    public required NamedValueName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static WorkspaceNamedValueDirectory From(NamedValueName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceNamedValuesDirectory.From(workspaceName, serviceDirectory),
            Name = name
        };

    public static Option<WorkspaceNamedValueDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in WorkspaceNamedValuesDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new WorkspaceNamedValueDirectory
        {
            Parent = parent,
            Name = NamedValueName.From(directory!.Name)
        };
}

public sealed record WorkspaceNamedValueInformationFile : ResourceFile
{
    public required WorkspaceNamedValueDirectory Parent { get; init; }

    private static string Name { get; } = "namedValueInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceNamedValueInformationFile From(NamedValueName name, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceNamedValueDirectory.From(name, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceNamedValueInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
            ? from parent in WorkspaceNamedValueDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceNamedValueInformationFile { Parent = parent }
            : Option<WorkspaceNamedValueInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record WorkspaceNamedValueDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required NamedValueContract Properties { get; init; }

    public sealed record NamedValueContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("keyVault")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public KeyVaultContract? KeyVault { get; init; }

        [JsonPropertyName("secret")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? Secret { get; init; }

        [JsonPropertyName("tags")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ImmutableArray<string>? Tags { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }

    public sealed record KeyVaultContract
    {
        [JsonPropertyName("identityClientId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? IdentityClientId { get; init; }

        [JsonPropertyName("secretIdentifier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? SecretIdentifier { get; init; }
    }
}

public static class WorkspaceNamedValueModule
{
    public static IAsyncEnumerable<NamedValueName> ListNames(this WorkspaceNamedValuesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(NamedValueName.From);

    public static IAsyncEnumerable<(NamedValueName Name, WorkspaceNamedValueDto Dto)> List(this WorkspaceNamedValuesUri workspaceNamedValuesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        workspaceNamedValuesUri.ListNames(pipeline, cancellationToken)
                               .SelectAwait(async name =>
                               {
                                   var uri = new WorkspaceNamedValueUri { Parent = workspaceNamedValuesUri, Name = name };
                                   var dto = await uri.GetDto(pipeline, cancellationToken);
                                   return (name, dto);
                               });

    public static async ValueTask<WorkspaceNamedValueDto> GetDto(this WorkspaceNamedValueUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<WorkspaceNamedValueDto>();
    }

    public static async ValueTask Delete(this WorkspaceNamedValueUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this WorkspaceNamedValueUri uri, WorkspaceNamedValueDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static async ValueTask WriteDto(this WorkspaceNamedValueInformationFile file, WorkspaceNamedValueDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<WorkspaceNamedValueDto> ReadDto(this WorkspaceNamedValueInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<WorkspaceNamedValueDto>();
    }
}