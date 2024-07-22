using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ApiTagsUri : ResourceUri
{
    public required ApiUri Parent { get; init; }

    private static string PathSegment { get; } = "tags";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ApiTagsUri From(ApiName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ApiUri.From(name, serviceUri) };
}

public sealed record ApiTagUri : ResourceUri
{
    public required ApiTagsUri Parent { get; init; }
    public required TagName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ApiTagUri From(TagName name, ApiName apiName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ApiTagsUri.From(apiName, serviceUri),
            Name = name
        };
}

public sealed record ApiTagsDirectory : ResourceDirectory
{
    public required ApiDirectory Parent { get; init; }
    private static string Name { get; } = "tags";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ApiTagsDirectory From(ApiName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ApiDirectory.From(name, serviceDirectory) };

    public static Option<ApiTagsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in ApiDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ApiTagsDirectory { Parent = parent }
            : Option<ApiTagsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record ApiTagDirectory : ResourceDirectory
{
    public required ApiTagsDirectory Parent { get; init; }

    public required TagName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ApiTagDirectory From(TagName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiTagsDirectory.From(apiName, serviceDirectory),
            Name = name
        };

    public static Option<ApiTagDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseApiTagName(directory)
        from parent in ApiTagsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ApiTagDirectory
        {
            Parent = parent,
            Name = name
        };

    internal static Option<TagName> TryParseApiTagName(DirectoryInfo? directory) =>
        string.IsNullOrWhiteSpace(directory?.Name)
        ? Option<TagName>.None
        : TagName.From(directory.Name);
}

public sealed record ApiTagInformationFile : ResourceFile
{
    public required ApiTagDirectory Parent { get; init; }

    private static string Name { get; } = "apiTagInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ApiTagInformationFile From(TagName name, ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ApiTagDirectory.From(name, apiName, serviceDirectory)
        };

    public static Option<ApiTagInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
        ? from parent in ApiTagDirectory.TryParse(file.Directory, serviceDirectory)
          select new ApiTagInformationFile { Parent = parent }
          : Option<ApiTagInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record ApiTagDto
{
    public static ApiTagDto Instance { get; } = new();
}

public static class ApiTagModule
{
    public static IAsyncEnumerable<TagName> ListNames(this ApiTagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(TagName.From);

    public static IAsyncEnumerable<(TagName Name, ApiTagDto Dto)> List(this ApiTagsUri apiTagsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        apiTagsUri.ListNames(pipeline, cancellationToken)
                        .Select(name => (name, ApiTagDto.Instance));

    public static async ValueTask Delete(this ApiTagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ApiTagUri uri, ApiTagDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ApiTagInformationFile> ListInformationFiles(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        ListApiTagsDirectories(apiName, serviceDirectory)
            .SelectMany(ListApiTagDirectories)
            .Select(directory => ApiTagInformationFile.From(directory.Name, apiName, serviceDirectory))
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    private static IEnumerable<ApiTagsDirectory> ListApiTagsDirectories(ApiName apiName, ManagementServiceDirectory serviceDirectory) =>
        ApiDirectory.From(apiName, serviceDirectory)
                    .ToDirectoryInfo()
                    .ListDirectories("*")
                    .Where(ApiTagsDirectory.IsDirectoryNameValid)
                    .Select(_ => ApiTagsDirectory.From(apiName, serviceDirectory));

    private static IEnumerable<ApiTagDirectory> ListApiTagDirectories(ApiTagsDirectory apiTagsDirectory) =>
        apiTagsDirectory.ToDirectoryInfo()
                        .ListDirectories("*")
                        .Choose(directory => from name in ApiTagDirectory.TryParseApiTagName(directory)
                                             select new ApiTagDirectory { Name = name, Parent = apiTagsDirectory });

    public static async ValueTask WriteDto(this ApiTagInformationFile file, ApiTagDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ApiTagDto> ReadDto(this ApiTagInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ApiTagDto>();
    }
}