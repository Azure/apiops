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

public sealed record NamedValueName : ResourceName, IResourceName<NamedValueName>
{
    private NamedValueName(string value) : base(value) { }

    public static NamedValueName From(string value) => new(value);
}

public sealed record NamedValuesUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "namedValues";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static NamedValuesUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record NamedValueUri : ResourceUri
{
    public required NamedValuesUri Parent { get; init; }
    public required NamedValueName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static NamedValueUri From(NamedValueName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = NamedValuesUri.From(serviceUri),
            Name = name
        };
}

public sealed record NamedValuesDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "named values";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static NamedValuesDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<NamedValuesDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new NamedValuesDirectory { ServiceDirectory = serviceDirectory }
            : Option<NamedValuesDirectory>.None;
}

public sealed record NamedValueDirectory : ResourceDirectory
{
    public required NamedValuesDirectory Parent { get; init; }

    public required NamedValueName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static NamedValueDirectory From(NamedValueName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = NamedValuesDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<NamedValueDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in NamedValuesDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new NamedValueDirectory
        {
            Parent = parent,
            Name = NamedValueName.From(directory!.Name)
        };
}

public sealed record NamedValueInformationFile : ResourceFile
{
    public required NamedValueDirectory Parent { get; init; }
    private static string Name { get; } = "namedValueInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static NamedValueInformationFile From(NamedValueName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new NamedValueDirectory
            {
                Parent = NamedValuesDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<NamedValueInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in NamedValueDirectory.TryParse(file.Directory, serviceDirectory)
              select new NamedValueInformationFile { Parent = parent }
            : Option<NamedValueInformationFile>.None;
}

public sealed record NamedValueDto
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

public static class NamedValueModule
{
    public static async ValueTask DeleteAll(this NamedValuesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await NamedValueUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<NamedValueName> ListNames(this NamedValuesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(NamedValueName.From);

    public static IAsyncEnumerable<(NamedValueName Name, NamedValueDto Dto)> List(this NamedValuesUri namedValuesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        namedValuesUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new NamedValueUri { Parent = namedValuesUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<NamedValueDto>> TryGetDto(this NamedValueUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<NamedValueDto>());
    }

    public static async ValueTask<NamedValueDto> GetDto(this NamedValueUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<NamedValueDto>();
    }

    public static async ValueTask Delete(this NamedValueUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this NamedValueUri uri, NamedValueDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<NamedValueDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var namedValuesDirectory = NamedValuesDirectory.From(serviceDirectory);

        return namedValuesDirectory.ToDirectoryInfo()
                                   .ListDirectories("*")
                                   .Select(directoryInfo => NamedValueName.From(directoryInfo.Name))
                                   .Select(name => new NamedValueDirectory { Parent = namedValuesDirectory, Name = name });
    }

    public static IEnumerable<NamedValueInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new NamedValueInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this NamedValueInformationFile file, NamedValueDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<NamedValueDto> ReadDto(this NamedValueInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<NamedValueDto>();
    }
}