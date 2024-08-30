using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record WorkspaceNamedValueName : ResourceName, IResourceName<WorkspaceNamedValueName>
{
    private WorkspaceNamedValueName(string value) : base(value) { }

    public static WorkspaceNamedValueName From(string value) => new(value);
}

public sealed record WorkspaceNamedValuesUri : ResourceUri
{
    public required WorkspaceUri Parent { get; init; }

    private static string PathSegment { get; } = "namedValues";

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static WorkspaceNamedValuesUri From(WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new() { Parent = WorkspaceUri.From(workspaceName, serviceUri) };
}

public sealed record WorkspaceNamedValueUri : ResourceUri
{
    public required WorkspaceNamedValuesUri Parent { get; init; }

    public required WorkspaceNamedValueName Name { get; init; }

    protected override Uri Value =>
        Parent.ToUri().AppendPathSegment(Name.Value).ToUri();

    public static WorkspaceNamedValueUri From(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = WorkspaceNamedValuesUri.From(workspaceName, serviceUri),
            Name = workspaceNamedValueName
        };
}

public sealed record WorkspaceNamedValuesDirectory : ResourceDirectory
{
    public required WorkspaceDirectory Parent { get; init; }

    private static string Name { get; } = "named values";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static WorkspaceNamedValuesDirectory From(WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = WorkspaceDirectory.From(workspaceName, serviceDirectory) };

    public static Option<WorkspaceNamedValuesDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name
            ? from parent in WorkspaceDirectory.TryParse(directory.Parent, serviceDirectory)
              select new WorkspaceNamedValuesDirectory { Parent = parent }
            : Option<WorkspaceNamedValuesDirectory>.None;
}

public sealed record WorkspaceNamedValueDirectory : ResourceDirectory
{
    public required WorkspaceNamedValuesDirectory Parent { get; init; }

    public required WorkspaceNamedValueName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.Value);

    public static WorkspaceNamedValueDirectory From(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceNamedValuesDirectory.From(workspaceName, serviceDirectory),
            Name = workspaceNamedValueName
        };

    public static Option<WorkspaceNamedValueDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is null
            ? Option<WorkspaceNamedValueDirectory>.None
            : from parent in WorkspaceNamedValuesDirectory.TryParse(directory.Parent, serviceDirectory)
              let name = WorkspaceNamedValueName.From(directory.Name)
              select new WorkspaceNamedValueDirectory
              {
                  Parent = parent,
                  Name = name
              };
}

public sealed record WorkspaceNamedValueInformationFile : ResourceFile
{
    public required WorkspaceNamedValueDirectory Parent { get; init; }

    private static string Name { get; } = "namedValueInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static WorkspaceNamedValueInformationFile From(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = WorkspaceNamedValueDirectory.From(workspaceNamedValueName, workspaceName, serviceDirectory)
        };

    public static Option<WorkspaceNamedValueInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in WorkspaceNamedValueDirectory.TryParse(file.Directory, serviceDirectory)
              select new WorkspaceNamedValueInformationFile
              {
                  Parent = parent
              }
            : Option<WorkspaceNamedValueInformationFile>.None;
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
    public static IAsyncEnumerable<WorkspaceNamedValueName> ListNames(this WorkspaceNamedValuesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(WorkspaceNamedValueName.From);

    public static IAsyncEnumerable<(WorkspaceNamedValueName Name, WorkspaceNamedValueDto Dto)> List(this WorkspaceNamedValuesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        uri.ListNames(pipeline, cancellationToken)
           .SelectAwait(async name =>
           {
               var resourceUri = new WorkspaceNamedValueUri { Parent = uri, Name = name };
               var dto = await resourceUri.GetDto(pipeline, cancellationToken);
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

    public static IEnumerable<WorkspaceNamedValueDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory) =>
        from workspaceDirectory in WorkspaceModule.ListDirectories(serviceDirectory)
        let workspaceNamedValuesDirectory = new WorkspaceNamedValuesDirectory { Parent = workspaceDirectory }
        where workspaceNamedValuesDirectory.ToDirectoryInfo().Exists()
        from workspaceNamedValueDirectoryInfo in workspaceNamedValuesDirectory.ToDirectoryInfo().ListDirectories("*")
        let name = WorkspaceNamedValueName.From(workspaceNamedValueDirectoryInfo.Name)
        select new WorkspaceNamedValueDirectory
        {
            Parent = workspaceNamedValuesDirectory,
            Name = name
        };

    public static IEnumerable<WorkspaceNamedValueInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        from workspaceNamedValueDirectory in ListDirectories(serviceDirectory)
        let informationFile = new WorkspaceNamedValueInformationFile { Parent = workspaceNamedValueDirectory }
        where informationFile.ToFileInfo().Exists()
        select informationFile;

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