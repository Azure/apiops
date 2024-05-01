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

public sealed record ProductTagsUri : ResourceUri
{
    public required ProductUri Parent { get; init; }

    private static string PathSegment { get; } = "tags";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ProductTagsUri From(ProductName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ProductUri.From(name, serviceUri) };
}

public sealed record ProductTagUri : ResourceUri
{
    public required ProductTagsUri Parent { get; init; }
    public required TagName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ProductTagUri From(TagName name, ProductName productName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ProductTagsUri.From(productName, serviceUri),
            Name = name
        };
}

public sealed record ProductTagsDirectory : ResourceDirectory
{
    public required ProductDirectory Parent { get; init; }
    private static string Name { get; } = "tags";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ProductTagsDirectory From(ProductName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ProductDirectory.From(name, serviceDirectory) };

    public static Option<ProductTagsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in ProductDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ProductTagsDirectory { Parent = parent }
            : Option<ProductTagsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record ProductTagDirectory : ResourceDirectory
{
    public required ProductTagsDirectory Parent { get; init; }

    public required TagName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ProductTagDirectory From(TagName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductTagsDirectory.From(productName, serviceDirectory),
            Name = name
        };

    public static Option<ProductTagDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseProductTagName(directory)
        from parent in ProductTagsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ProductTagDirectory
        {
            Parent = parent,
            Name = name
        };

    internal static Option<TagName> TryParseProductTagName(DirectoryInfo? directory) =>
        string.IsNullOrWhiteSpace(directory?.Name)
        ? Option<TagName>.None
        : TagName.From(directory.Name);
}

public sealed record ProductTagInformationFile : ResourceFile
{
    public required ProductTagDirectory Parent { get; init; }

    private static string Name { get; } = "productTagInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ProductTagInformationFile From(TagName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductTagDirectory.From(name, productName, serviceDirectory)
        };

    public static Option<ProductTagInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
        ? from parent in ProductTagDirectory.TryParse(file.Directory, serviceDirectory)
          select new ProductTagInformationFile { Parent = parent }
          : Option<ProductTagInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record ProductTagDto
{
    public static ProductTagDto Instance { get; } = new();
}

public static class ProductTagModule
{
    public static IAsyncEnumerable<TagName> ListNames(this ProductTagsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(TagName.From);

    public static IAsyncEnumerable<(TagName Name, ProductTagDto Dto)> List(this ProductTagsUri productTagsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        productTagsUri.ListNames(pipeline, cancellationToken)
                        .Select(name => (name, ProductTagDto.Instance));

    public static async ValueTask Delete(this ProductTagUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ProductTagUri uri, ProductTagDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ProductTagInformationFile> ListInformationFiles(ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(productName, serviceDirectory)
            .Select(directory => ProductTagInformationFile.From(directory.Name, productName, serviceDirectory));

    private static IEnumerable<ProductTagDirectory> ListDirectories(ProductName productName, ManagementServiceDirectory serviceDirectory)
    {
        var parentDirectory = ProductTagsDirectory.From(productName, serviceDirectory);

        return parentDirectory.ToDirectoryInfo()
                              .ListDirectories("*")
                              .Choose(ProductTagDirectory.TryParseProductTagName)
                              .Select(tagName => new ProductTagDirectory
                              {
                                  Name = tagName,
                                  Parent = parentDirectory
                              });
    }

    public static async ValueTask WriteDto(this ProductTagInformationFile file, ProductTagDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ProductTagDto> ReadDto(this ProductTagInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ProductTagDto>();
    }
}