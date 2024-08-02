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

public sealed record VersionSetName : ResourceName, IResourceName<VersionSetName>
{
    private VersionSetName(string value) : base(value) { }

    public static VersionSetName From(string value) => new(value);
}

public sealed record VersionSetsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "apiVersionSets";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static VersionSetsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record VersionSetUri : ResourceUri
{
    public required VersionSetsUri Parent { get; init; }
    public required VersionSetName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static VersionSetUri From(VersionSetName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = VersionSetsUri.From(serviceUri),
            Name = name
        };
}

public sealed record VersionSetsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "version sets";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static VersionSetsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<VersionSetsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new VersionSetsDirectory { ServiceDirectory = serviceDirectory }
            : Option<VersionSetsDirectory>.None;
}

public sealed record VersionSetDirectory : ResourceDirectory
{
    public required VersionSetsDirectory Parent { get; init; }

    public required VersionSetName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static VersionSetDirectory From(VersionSetName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = VersionSetsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<VersionSetDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in VersionSetsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new VersionSetDirectory
        {
            Parent = parent,
            Name = VersionSetName.From(directory!.Name)
        };
}

public sealed record VersionSetInformationFile : ResourceFile
{
    public required VersionSetDirectory Parent { get; init; }
    private static string Name { get; } = "versionSetInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static VersionSetInformationFile From(VersionSetName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new VersionSetDirectory
            {
                Parent = VersionSetsDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<VersionSetInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in VersionSetDirectory.TryParse(file.Directory, serviceDirectory)
              select new VersionSetInformationFile { Parent = parent }
            : Option<VersionSetInformationFile>.None;
}

public sealed record VersionSetDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required VersionSetContract Properties { get; init; }

    public sealed record VersionSetContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("versioningScheme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersioningScheme { get; init; }

        [JsonPropertyName("versionQueryName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersionQueryName { get; init; }

        [JsonPropertyName("versionHeaderName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? VersionHeaderName { get; init; }
    }
}

public static class VersionSetModule
{
    public static async ValueTask DeleteAll(this VersionSetsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await VersionSetUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<VersionSetName> ListNames(this VersionSetsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(VersionSetName.From);

    public static IAsyncEnumerable<(VersionSetName Name, VersionSetDto Dto)> List(this VersionSetsUri versionSetsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        versionSetsUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new VersionSetUri { Parent = versionSetsUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<VersionSetDto>> TryGetDto(this VersionSetUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<VersionSetDto>());
    }

    public static async ValueTask<VersionSetDto> GetDto(this VersionSetUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<VersionSetDto>();
    }

    public static async ValueTask Delete(this VersionSetUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this VersionSetUri uri, VersionSetDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<VersionSetDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var versionSetsDirectory = VersionSetsDirectory.From(serviceDirectory);

        return versionSetsDirectory.ToDirectoryInfo()
                                   .ListDirectories("*")
                                   .Select(directoryInfo => VersionSetName.From(directoryInfo.Name))
                                   .Select(name => new VersionSetDirectory { Parent = versionSetsDirectory, Name = name });
    }

    public static IEnumerable<VersionSetInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new VersionSetInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this VersionSetInformationFile file, VersionSetDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<VersionSetDto> ReadDto(this VersionSetInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<VersionSetDto>();
    }
}