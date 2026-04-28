using Azure.Core.Pipeline;
using Flurl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record TagApiResource : ICompositeResource
{
    private TagApiResource() { }

    public string FileName { get; } = "tagApiInformation.json";

    public IResourceWithDirectory Primary { get; } = TagResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ApiResource.Instance;

    public static TagApiResource Instance { get; } = new();
}

public static partial class ResourceModule
{
    private static IAsyncEnumerable<ResourceName> ListTagApiNamesFromApim(ParentChain parents, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        ListTagApiDtosFromApim(parents, serviceUri, pipeline, cancellationToken)
            .Select(tuple => tuple.Name);

    /// <summary>
    /// We get tag APIs by calling the tag's apiLinks endpoint
    /// </summary>
    private static IAsyncEnumerable<(ResourceName Name, JsonObject Dto)> ListTagApiDtosFromApim(ParentChain parents, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = parents.GetUri(serviceUri)
                         .AppendPathSegment("apiLinks")
                         .ToUri();

        return pipeline.ListJsonObjects(uri, cancellationToken)
                       .Select(dto => from properties in dto.GetJsonObjectProperty("properties")
                                      from apiId in properties.GetStringProperty("apiId")
                                      let nameString = apiId.Split('/').LastOrDefault()
                                      from name in ResourceName.From(nameString)
                                      let serializerOptions = ((IResourceWithDto)TagApiResource.Instance).SerializerOptions
                                      from json in JsonObjectModule.From(DirectCompositeDto.Instance, serializerOptions)
                                      select (name, json))
                       .Select(result => result.IfErrorThrow());
    }

    private static async ValueTask<bool> TagApiExistsInApim(ResourceName name, ParentChain ancestors, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = GetTagApiUriViaApiTags(name, ancestors, serviceUri);
        var result = await pipeline.Head(uri, cancellationToken);

        return result.IfErrorThrow()
                     .IsSome;
    }

    /// <summary>
    // Tag APIs are inconsistent in the APIM REST API. Some operations use /tags/tagName/apiLinks/linkName,
    // while others use /apis/apiName/tags/tagName. This method gets a URI using the latter approach.
    /// </summary>
    private static Uri GetTagApiUriViaApiTags(ResourceName name, ParentChain ancestors, ServiceUri serviceUri)
    {
        IResource resource = TagApiResource.Instance;
        var apiName = name;
        var tagName = ancestors.Last().Name;

        return resource.GetUri(apiName, ParentChain.Empty, serviceUri)
                       .AppendPathSegment(TagResource.Instance.CollectionUriPath)
                       .AppendPathSegment(tagName)
                       .ToUri();
    }

    private static async ValueTask PutTagApiInApim(ResourceName name,
                                                   JsonObject dto,
                                                   ParentChain ancestors,
                                                   HttpPipeline pipeline,
                                                   ServiceUri serviceUri,
                                                   CancellationToken cancellationToken)
    {
        var uri = GetTagApiUriViaApiTags(name, ancestors, serviceUri);
        var result = await pipeline.PutJson(uri, dto, cancellationToken);
        result.IfErrorThrow();
    }

    private static async ValueTask DeleteTagApiFromApim(ResourceName name, ParentChain ancestors, HttpPipeline pipeline, ServiceUri serviceUri, bool ignoreNotFound, bool waitForCompletion, CancellationToken cancellationToken)
    {
        var uri = GetTagApiUriViaApiTags(name, ancestors, serviceUri);
        var result = await pipeline.Delete(uri, cancellationToken, ignoreNotFound, waitForCompletion);
        result.IfErrorThrow();
    }
}