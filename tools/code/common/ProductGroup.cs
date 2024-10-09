using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ProductGroupsUri : ResourceUri
{
    public required ProductUri Parent { get; init; }

    private static string PathSegment { get; } = "groups";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ProductGroupsUri From(ProductName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ProductUri.From(name, serviceUri) };
}

public sealed record ProductGroupUri : ResourceUri
{
    public required ProductGroupsUri Parent { get; init; }
    public required GroupName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ProductGroupUri From(GroupName name, ProductName productName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ProductGroupsUri.From(productName, serviceUri),
            Name = name
        };
}

public sealed record ProductGroupsDirectory : ResourceDirectory
{
    public required ProductDirectory Parent { get; init; }
    private static string Name { get; } = "groups";

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name);

    public static ProductGroupsDirectory From(ProductName name, ManagementServiceDirectory serviceDirectory) =>
        new() { Parent = ProductDirectory.From(name, serviceDirectory) };

    public static Option<ProductGroupsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        IsDirectoryNameValid(directory)
            ? from parent in ProductDirectory.TryParse(directory.Parent, serviceDirectory)
              select new ProductGroupsDirectory { Parent = parent }
            : Option<ProductGroupsDirectory>.None;

    internal static bool IsDirectoryNameValid([NotNullWhen(true)] DirectoryInfo? directory) =>
        directory?.Name == Name;
}

public sealed record ProductGroupDirectory : ResourceDirectory
{
    public required ProductGroupsDirectory Parent { get; init; }

    public required GroupName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ProductGroupDirectory From(GroupName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductGroupsDirectory.From(productName, serviceDirectory),
            Name = name
        };

    public static Option<ProductGroupDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseProductGroupName(directory)
        from parent in ProductGroupsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ProductGroupDirectory
        {
            Parent = parent,
            Name = name
        };

    internal static Option<GroupName> TryParseProductGroupName(DirectoryInfo? directory) =>
        string.IsNullOrWhiteSpace(directory?.Name)
        ? Option<GroupName>.None
        : GroupName.From(directory.Name);
}

public sealed record ProductGroupInformationFile : ResourceFile
{
    public required ProductGroupDirectory Parent { get; init; }

    private static string Name { get; } = "productGroupInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ProductGroupInformationFile From(GroupName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductGroupDirectory.From(name, productName, serviceDirectory)
        };

    public static Option<ProductGroupInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        IsFileNameValid(file)
        ? from parent in ProductGroupDirectory.TryParse(file.Directory, serviceDirectory)
          select new ProductGroupInformationFile { Parent = parent }
          : Option<ProductGroupInformationFile>.None;

    internal static bool IsFileNameValid([NotNullWhen(true)] FileInfo? file) =>
        file?.Name == Name;
}

public sealed record ProductGroupDto
{
    public static ProductGroupDto Instance { get; } = new();
}

public static class ProductGroupModule
{
    public static IAsyncEnumerable<GroupName> ListNames(this ProductGroupsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var exceptionHandler = (HttpRequestException exception) =>
            exception.StatusCode == HttpStatusCode.BadRequest
             && exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase)
            ? AsyncEnumerable.Empty<GroupName>()
            : throw exception;

        return pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(GroupName.From)
                .Catch(exceptionHandler);
    }

    public static IAsyncEnumerable<(GroupName Name, ProductGroupDto Dto)> List(this ProductGroupsUri productGroupsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        productGroupsUri.ListNames(pipeline, cancellationToken)
                        .Select(name => (name, ProductGroupDto.Instance));

    public static async ValueTask Delete(this ProductGroupUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ProductGroupUri uri, ProductGroupDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ProductGroupInformationFile> ListInformationFiles(ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        ListProductGroupsDirectories(productName, serviceDirectory)
            .SelectMany(ListProductGroupDirectories)
            .Select(directory => ProductGroupInformationFile.From(directory.Name, productName, serviceDirectory));

    private static IEnumerable<ProductGroupsDirectory> ListProductGroupsDirectories(ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        ProductDirectory.From(productName, serviceDirectory)
                        .ToDirectoryInfo()
                        .ListDirectories("*")
                        .Where(ProductGroupsDirectory.IsDirectoryNameValid)
                        .Select(_ => ProductGroupsDirectory.From(productName, serviceDirectory));

    private static IEnumerable<ProductGroupDirectory> ListProductGroupDirectories(ProductGroupsDirectory productGroupsDirectory) =>
        productGroupsDirectory.ToDirectoryInfo()
                              .ListDirectories("*")
                              .Choose(directory => from name in ProductGroupDirectory.TryParseProductGroupName(directory)
                                                   select new ProductGroupDirectory { Name = name, Parent = productGroupsDirectory });

    public static async ValueTask WriteDto(this ProductGroupInformationFile file, ProductGroupDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ProductGroupDto> ReadDto(this ProductGroupInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ProductGroupDto>();
    }
}