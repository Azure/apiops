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

public sealed record TagName : ResourceName, IResourceName<TagName>
{
    private TagName(string value) : base(value) { }

    public static TagName From(string value) => new(value);
}

public sealed record TagsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "tags";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static TagsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record TagUri : ResourceUri
{
    public required TagsUri Parent { get; init; }
    public required TagName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static TagUri From(TagName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = TagsUri.From(serviceUri),
            Name = name
        };
}

public sealed record TagsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "tags";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static TagsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<TagsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory?.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new TagsDirectory { ServiceDirectory = serviceDirectory }
            : Option<TagsDirectory>.None;
}

public sealed record TagDirectory : ResourceDirectory
{
    public required TagsDirectory Parent { get; init; }

    public required TagName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static TagDirectory From(TagName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = TagsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<TagDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in TagsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new TagDirectory
        {
            Parent = parent,
            Name = TagName.From(directory!.Name)
        };
}

public sealed record TagInformationFile : ResourceFile
{
    public required TagDirectory Parent { get; init; }
    private static string Name { get; } = "tagInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static TagInformationFile From(TagName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = TagDirectory.From(name, serviceDirectory)
        };

    public static Option<TagInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file?.Name == Name
            ? from parent in TagDirectory.TryParse(file.Directory, serviceDirectory)
              select new TagInformationFile { Parent = parent }
            : Option<TagInformationFile>.None;
}

public sealed record TagDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required TagContract Properties { get; init; }

    public sealed record TagContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }
    }
}

public static class TagModule
{
    public static async ValueTask DeleteAll(this TagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await TagUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<TagName> ListNames(this TagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(TagName.From);

    public static IAsyncEnumerable<(TagName Name, TagDto Dto)> List(this TagsUri tagsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        tagsUri.ListNames(pipeline, cancellationToken)
               .SelectAwait(async name =>
               {
                   var uri = new TagUri { Parent = tagsUri, Name = name };
                   var dto = await uri.GetDto(pipeline, cancellationToken);
                   return (name, dto);
               });

    public static async ValueTask<Option<TagDto>> TryGetDto(this TagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<TagDto>());
    }

    public static async ValueTask<TagDto> GetDto(this TagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<TagDto>();
    }

    public static async ValueTask Delete(this TagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this TagUri uri, TagDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<TagDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var tagsDirectory = TagsDirectory.From(serviceDirectory);

        return tagsDirectory.ToDirectoryInfo()
                            .ListDirectories("*")
                            .Select(directoryInfo => TagName.From(directoryInfo.Name))
                            .Select(name => new TagDirectory { Parent = tagsDirectory, Name = name });
    }

    public static IEnumerable<TagInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new TagInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this TagInformationFile file, TagDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<TagDto> ReadDto(this TagInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<TagDto>();
    }
}