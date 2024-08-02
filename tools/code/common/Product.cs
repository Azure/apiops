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

public sealed record ProductName : ResourceName, IResourceName<ProductName>
{
    private ProductName(string value) : base(value) { }

    public static ProductName From(string value) => new(value);
}

public sealed record ProductsUri : ResourceUri
{
    public required ManagementServiceUri ServiceUri { get; init; }

    private static string PathSegment { get; } = "products";

    protected override Uri Value => ServiceUri.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ProductsUri From(ManagementServiceUri serviceUri) =>
        new() { ServiceUri = serviceUri };
}

public sealed record ProductUri : ResourceUri
{
    public required ProductsUri Parent { get; init; }
    public required ProductName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ProductUri From(ProductName name, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ProductsUri.From(serviceUri),
            Name = name
        };
}

public sealed record ProductsDirectory : ResourceDirectory
{
    public required ManagementServiceDirectory ServiceDirectory { get; init; }

    private static string Name { get; } = "products";

    protected override DirectoryInfo Value =>
        ServiceDirectory.ToDirectoryInfo().GetChildDirectory(Name);

    public static ProductsDirectory From(ManagementServiceDirectory serviceDirectory) =>
        new() { ServiceDirectory = serviceDirectory };

    public static Option<ProductsDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        directory is not null &&
        directory.Name == Name &&
        directory.Parent?.FullName == serviceDirectory.ToDirectoryInfo().FullName
            ? new ProductsDirectory { ServiceDirectory = serviceDirectory }
            : Option<ProductsDirectory>.None;
}

public sealed record ProductDirectory : ResourceDirectory
{
    public required ProductsDirectory Parent { get; init; }

    public required ProductName Name { get; init; }

    protected override DirectoryInfo Value =>
        Parent.ToDirectoryInfo().GetChildDirectory(Name.ToString());

    public static ProductDirectory From(ProductName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductsDirectory.From(serviceDirectory),
            Name = name
        };

    public static Option<ProductDirectory> TryParse(DirectoryInfo? directory, ManagementServiceDirectory serviceDirectory) =>
        from parent in ProductsDirectory.TryParse(directory?.Parent, serviceDirectory)
        select new ProductDirectory
        {
            Parent = parent,
            Name = ProductName.From(directory!.Name)
        };
}

public sealed record ProductInformationFile : ResourceFile
{
    public required ProductDirectory Parent { get; init; }
    private static string Name { get; } = "productInformation.json";

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile(Name);

    public static ProductInformationFile From(ProductName name, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = new ProductDirectory
            {
                Parent = ProductsDirectory.From(serviceDirectory),
                Name = name
            }
        };

    public static Option<ProductInformationFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        file is not null && file.Name == Name
            ? from parent in ProductDirectory.TryParse(file.Directory, serviceDirectory)
              select new ProductInformationFile { Parent = parent }
            : Option<ProductInformationFile>.None;
}

public sealed record ProductDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ProductContract Properties { get; init; }

    public record ProductContract
    {
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("approvalRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? ApprovalRequired { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? State { get; init; }

        [JsonPropertyName("subscriptionRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? SubscriptionRequired { get; init; }

        [JsonPropertyName("subscriptionsLimit")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? SubscriptionsLimit { get; init; }

        [JsonPropertyName("terms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Terms { get; init; }
    }
}

public static class ProductModule
{
    public static async ValueTask DeleteAll(this ProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await uri.ListNames(pipeline, cancellationToken)
                 .IterParallel(async name => await ProductUri.From(name, uri.ServiceUri)
                                                                .Delete(pipeline, cancellationToken),
                               cancellationToken);

    public static IAsyncEnumerable<ProductName> ListNames(this ProductsUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ProductName.From);

    public static IAsyncEnumerable<(ProductName Name, ProductDto Dto)> List(this ProductsUri productsUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        productsUri.ListNames(pipeline, cancellationToken)
                      .SelectAwait(async name =>
                      {
                          var uri = new ProductUri { Parent = productsUri, Name = name };
                          var dto = await uri.GetDto(pipeline, cancellationToken);
                          return (name, dto);
                      });

    public static async ValueTask<Option<ProductDto>> TryGetDto(this ProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentOption = await pipeline.GetContentOption(uri.ToUri(), cancellationToken);
        return contentOption.Map(content => content.ToObjectFromJson<ProductDto>());
    }

    public static async ValueTask<ProductDto> GetDto(this ProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = await pipeline.GetContent(uri.ToUri(), cancellationToken);
        return content.ToObjectFromJson<ProductDto>();
    }

    public static async ValueTask Delete(this ProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ProductUri uri, ProductDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IAsyncEnumerable<SubscriptionName> ListSubscriptionNames(this ProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri().AppendPathSegment("subscriptions").ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(SubscriptionName.From);

    public static IAsyncEnumerable<GroupName> ListGroupNames(this ProductUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri().AppendPathSegment("groups").ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(GroupName.From);

    public static IEnumerable<ProductDirectory> ListDirectories(ManagementServiceDirectory serviceDirectory)
    {
        var productsDirectory = ProductsDirectory.From(serviceDirectory);

        return productsDirectory.ToDirectoryInfo()
                                .ListDirectories("*")
                                .Select(directoryInfo => ProductName.From(directoryInfo.Name))
                                .Select(name => new ProductDirectory { Parent = productsDirectory, Name = name });
    }

    public static IEnumerable<ProductInformationFile> ListInformationFiles(ManagementServiceDirectory serviceDirectory) =>
        ListDirectories(serviceDirectory)
            .Select(directory => new ProductInformationFile { Parent = directory })
            .Where(informationFile => informationFile.ToFileInfo().Exists());

    public static async ValueTask WriteDto(this ProductInformationFile file, ProductDto dto, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto, JsonObjectExtensions.SerializerOptions);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<ProductDto> ReadDto(this ProductInformationFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToObjectFromJson<ProductDto>();
    }
}