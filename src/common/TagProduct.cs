using Azure.Core.Pipeline;
using Flurl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record TagProductResource : ICompositeResource
{
    private TagProductResource() { }

    public string FileName { get; } = "tagProductInformation.json";

    public IResourceWithDirectory Primary { get; } = TagResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ProductResource.Instance;

    public static TagProductResource Instance { get; } = new();
}

public static partial class ResourceModule
{
    private static IAsyncEnumerable<ResourceName> ListTagProductNamesFromApim(ParentChain parents, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        ListTagProductDtosFromApim(parents, serviceUri, pipeline, cancellationToken)
            .Select(tuple => tuple.Name);

    /// <summary>
    /// We get tag products by calling the tag's productLinks endpoint
    /// </summary>
    private static IAsyncEnumerable<(ResourceName Name, JsonObject Dto)> ListTagProductDtosFromApim(ParentChain parents, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = parents.GetUri(serviceUri)
                         .AppendPathSegment("productLinks")
                         .ToUri();

        return pipeline.ListJsonObjects(uri, cancellationToken)
                       .Select(dto => from properties in dto.GetJsonObjectProperty("properties")
                                      from productId in properties.GetStringProperty("productId")
                                      let nameString = productId.Split('/').LastOrDefault()
                                      from name in ResourceName.From(nameString)
                                      let serializerOptions = ((IResourceWithDto)TagApiResource.Instance).SerializerOptions
                                      from json in JsonObjectModule.From(DirectCompositeDto.Instance, serializerOptions)
                                      select (name, json))
                       .Select(result => result.IfErrorThrow());
    }

    private static async ValueTask<bool> TagProductExistsInApim(ResourceName name, ParentChain ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = GetTagProductUriViaProductTags(name, ancestors, serviceUri);
        var result = await pipeline.Head(uri, cancellationToken);

        return result.IfErrorThrow()
                     .IsSome;
    }

    /// <summary>
    // Tag products are inconsistent in the APIM REST API. Some operations use /tags/tagName/productLinks/linkName,
    // while others use /products/productName/tags/tagName. This method gets a URI using the latter approach.
    /// </summary>
    private static Uri GetTagProductUriViaProductTags(ResourceName name, ParentChain ancestors, ServiceUri serviceUri)
    {
        IResource resource = TagProductResource.Instance;
        var productName = name;
        var tagName = ancestors.Last().Name;

        return resource.GetUri(productName, ParentChain.Empty, serviceUri)
                       .AppendPathSegment(TagResource.Instance.CollectionUriPath)
                       .AppendPathSegment(tagName)
                       .ToUri();
    }

    private static async ValueTask PutTagProductInApim(ResourceName name,
                                                       JsonObject dto,
                                                       ParentChain ancestors,
                                                       HttpPipeline pipeline,
                                                       ServiceUri serviceUri,
                                                       CancellationToken cancellationToken)
    {
        var uri = GetTagProductUriViaProductTags(name, ancestors, serviceUri);
        var result = await pipeline.PutJson(uri, dto, cancellationToken);
        result.IfErrorThrow();
    }

    private static async ValueTask DeleteTagProductFromApim(ResourceName name, ParentChain ancestors, HttpPipeline pipeline, ServiceUri serviceUri, bool ignoreNotFound, bool waitForCompletion, CancellationToken cancellationToken)
    {
        var uri = GetTagProductUriViaProductTags(name, ancestors, serviceUri);
        var result = await pipeline.Delete(uri, cancellationToken, ignoreNotFound, waitForCompletion);
        result.IfErrorThrow();
    }
}