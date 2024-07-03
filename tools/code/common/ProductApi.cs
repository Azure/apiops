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

public sealed record ProductApisUri : ResourceUri
{
    public required ProductUri Parent { get; init; }

    private static string PathSegment { get; } = "apis";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ProductApisUri From(ProductName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ProductUri.From(name, serviceUri) };
}

public sealed record ProductApiUri : ResourceUri
{
    public required ProductApisUri Parent { get; init; }
    public required ApiName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ProductApiUri From(ApiName name, ProductName productName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ProductApisUri.From(productName, serviceUri),
            Name = name
        };
}

public sealed record ProductApisDirectory : ResourceDirectory
{
    public required ProductDirectory Parent { get; init; }
    private static string Name { get; } = "apis";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ProductApisDirectory From(ProductName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ProductDirectory.From(name, serviceDirectory) };

    public static Option<ProductApisDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in ProductDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ProductApisDirectory { Parent = parent }
            : Option<ProductApisDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record ProductApiDirectory : ResourceDirectory
{
    public required ProductApisDirectory Parent { get; init; }

    public required ApiName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ProductApiDirectory From(ApiName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductApisDirectory.From(productName, serviceDirectory),
            Name = name
        };

    public static Option<ProductApiDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseProductApiName(directory)
        from parent in ProductApisDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ProductApiDirectory
        {
            Parent = parent,
            Name = name
        };

    internal static Option<ApiName> TryParseProductApiName(DirectoryInfo? directory) =>
        string.IsNullOrWhiteSpace(directory?.Name)
        ? Option<ApiName>.None
        : ApiName.From(directory.Name);
}

public sealed record ProductApiInformationFile : ResourceFile
{
    public required ProductApiDirectory Parent { get; init; }

    private static string Name { get; } = "productApiInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ProductApiInformationFile From(ApiName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductApiDirectory.From(name, productName, serviceDirectory)
        };

    public static Option<ProductApiInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
        ? from parent in ProductApiDirectory.TryParse(file.Directory, serviceDirectory)
          select new ProductApiInformationFile { Parent = parent }
          : Option<ProductApiInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record ProductApiDto
{
    public static ProductApiDto Instance { get; } = new();
}

public static class ProductApiModule
{
    public static IAsyncEnumerable<ApiName> ListNames(this ProductApisUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ApiName.From)
                .Where(ApiName.IsNotRevisioned);

    public static IAsyncEnumerable<(ApiName Name, ProductApiDto Dto)> List(this ProductApisUri productApisUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        productApisUri.ListNames(pipeline, cancellationToken)
                        .Select(name => (name, ProductApiDto.Instance));

    public static async ValueTask Delete(this ProductApiUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ProductApiUri uri, ProductApiDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ProductApiInformationFile> ListInformationFiles(ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        ListProductApisDirectories(productName, serviceDirectory)
            .SelectMany(ListProductApiDirectories)
            .Select(directory => ProductApiInformationFile.From(directory.Name, productName, serviceDirectory));

    private static IEnumerable<ProductApisDirectory> ListProductApisDirectories(ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        ProductDirectory.From(productName, serviceDirectory)
                        .ToDirectoryInfo()
                        .ListDirectories("*")
                        .Where(ProductApisDirectory.IsDirectoryNameValid)
                        .Select(_ => ProductApisDirectory.From(productName, serviceDirectory));

    private static IEnumerable<ProductApiDirectory> ListProductApiDirectories(ProductApisDirectory productApisDirectory) =>
        productApisDirectory.ToDirectoryInfo()
                              .ListDirectories("*")
                              .Choose(directory => from name in ProductApiDirectory.TryParseProductApiName(directory)
                                                   select new ProductApiDirectory { Name = name, Parent = productApisDirectory });

    public static async ValueTask WriteDto(this ProductApiInformationFile file, ProductApiDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ProductApiDto> ReadDto(this ProductApiInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ProductApiDto>();
    }
}