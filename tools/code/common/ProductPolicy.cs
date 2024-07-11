using Azure.Core.Pipeline;
using Flurl;
using LanguageExt;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ProductPolicyName : ResourceName
{
    private ProductPolicyName(string value) : base(value) { }

    public static ProductPolicyName From(string value) => new(value);
}

public sealed record ProductPoliciesUri : ResourceUri
{
    public required ProductUri Parent { get; init; }

    private static string PathSegment { get; } = "policies";

    protected override Uri Value => Parent.ToUri().AppendPathSegment(PathSegment).ToUri();

    public static ProductPoliciesUri From(ProductName name, ManagementServiceUri serviceUri) =>
        new() { Parent = ProductUri.From(name, serviceUri) };
}

public sealed record ProductPolicyUri : ResourceUri
{
    public required ProductPoliciesUri Parent { get; init; }
    public required ProductPolicyName Name { get; init; }

    protected override Uri Value => Parent.ToUri().AppendPathSegment(Name.ToString()).ToUri();

    public static ProductPolicyUri From(ProductPolicyName name, ProductName productName, ManagementServiceUri serviceUri) =>
        new()
        {
            Parent = ProductPoliciesUri.From(productName, serviceUri),
            Name = name
        };
}

public sealed record ProductPolicyFile : ResourceFile
{
    public required ProductDirectory Parent { get; init; }
    public required ProductPolicyName Name { get; init; }

    protected override FileInfo Value =>
        Parent.ToDirectoryInfo().GetChildFile($"{Name}.xml");

    public static ProductPolicyFile From(ProductPolicyName name, ProductName productName, ManagementServiceDirectory serviceDirectory) =>
        new()
        {
            Parent = ProductDirectory.From(productName, serviceDirectory),
            Name = name
        };

    public static Option<ProductPolicyFile> TryParse(FileInfo? file, ManagementServiceDirectory serviceDirectory) =>
        from name in TryParseProductPolicyName(file)
        from parent in ProductDirectory.TryParse(file?.Directory, serviceDirectory)
        select new ProductPolicyFile
        {
            Name = name,
            Parent = parent
        };

    internal static Option<ProductPolicyName> TryParseProductPolicyName(FileInfo? file) =>
        file?.Name.EndsWith(".xml", StringComparison.Ordinal) switch
        {
            true => ProductPolicyName.From(Path.GetFileNameWithoutExtension(file.Name)),
            _ => Option<ProductPolicyName>.None
        };
}

public sealed record ProductPolicyDto
{
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public required ProductPolicyContract Properties { get; init; }

    public sealed record ProductPolicyContract
    {
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Format { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Value { get; init; }
    }
}

public static class ProductPolicyModule
{
    public static IAsyncEnumerable<ProductPolicyName> ListNames(this ProductPoliciesUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        pipeline.ListJsonObjects(uri.ToUri(), cancellationToken)
                .Select(jsonObject => jsonObject.GetStringProperty("name"))
                .Select(ProductPolicyName.From);

    public static IAsyncEnumerable<(ProductPolicyName Name, ProductPolicyDto Dto)> List(this ProductPoliciesUri productPoliciesUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        productPoliciesUri.ListNames(pipeline, cancellationToken)
                          .SelectAwait(async name =>
                          {
                              var uri = new ProductPolicyUri { Parent = productPoliciesUri, Name = name };
                              var dto = await uri.GetDto(pipeline, cancellationToken);
                              return (name, dto);
                          });

    public static async ValueTask<ProductPolicyDto> GetDto(this ProductPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var contentUri = uri.ToUri().AppendQueryParam("format", "rawxml").ToUri();
        var content = await pipeline.GetContent(contentUri, cancellationToken);
        return content.ToObjectFromJson<ProductPolicyDto>();
    }

    public static async ValueTask Delete(this ProductPolicyUri uri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(uri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask PutDto(this ProductPolicyUri uri, ProductPolicyDto dto, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(dto);
        await pipeline.PutContent(uri.ToUri(), content, cancellationToken);
    }

    public static IEnumerable<ProductPolicyFile> ListPolicyFiles(ProductName productName, ManagementServiceDirectory serviceDirectory)
    {
        var productDirectory = ProductDirectory.From(productName, serviceDirectory);

        return productDirectory.ToDirectoryInfo()
                               .ListFiles("*")
                               .Choose(ProductPolicyFile.TryParseProductPolicyName)
                               .Select(name => new ProductPolicyFile { Name = name, Parent = productDirectory });
    }

    public static async ValueTask WritePolicy(this ProductPolicyFile file, string policy, CancellationToken cancellationToken)
    {
        var content = BinaryData.FromString(policy);
        await file.ToFileInfo().OverwriteWithBinaryData(content, cancellationToken);
    }

    public static async ValueTask<string> ReadPolicy(this ProductPolicyFile file, CancellationToken cancellationToken)
    {
        var content = await file.ToFileInfo().ReadAsBinaryData(cancellationToken);
        return content.ToString();
    }
}