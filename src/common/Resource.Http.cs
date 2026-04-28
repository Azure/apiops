using Azure.Core.Pipeline;
using DotNext.Threading;
using Flurl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public delegate ValueTask<bool> IsResourceSupportedInApim(IResource resource, CancellationToken cancellationToken);
public delegate ValueTask<Option<JsonObject>> GetOptionalResourceDtoFromApim(IResourceWithDto resource, ResourceName name, ParentChain parents, CancellationToken cancellationToken);
public delegate ValueTask<JsonObject> GetResourceDtoFromApim(IResourceWithDto resource, ResourceName name, ParentChain parents, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<ResourceName> ListResourceNamesFromApim(IResource resource, ParentChain parents, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ResourceName Name, JsonObject Dto)> ListResourceDtosFromApim(IResourceWithDto resource, ParentChain parents, CancellationToken cancellationToken);
public delegate ValueTask<bool> DoesResourceExistInApim(ResourceKey resourceKey, CancellationToken cancellationToken);
public delegate ValueTask PutResourceInApim(IResourceWithDto resource, ResourceName name, JsonObject dto, ParentChain parents, CancellationToken cancellationToken);
public delegate ValueTask DeleteResourceFromApim(ResourceKey resourceKey, bool ignoreNotFound, bool waitForCompletion, CancellationToken cancellationToken);

public static partial class ResourceModule
{
    public static void ConfigureIsResourceSupportedInApim(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveIsResourceSupportedInApim);
    }

    private static IsResourceSupportedInApim ResolveIsResourceSupportedInApim(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var cache = new ConcurrentDictionary<IResource, AsyncLazy<bool>>();

        return isResourceSupported;

        async ValueTask<bool> isResourceSupported(IResource resource, CancellationToken cancellationToken) =>
            await cache.GetOrAdd(resource, _ => new(async cancellationToken =>
                                 {
                                     // If the resource is a root resource, we can directly check if it's supported.
                                     var rootResources = graph.ListTraversalRootResources();
                                     if (rootResources.Contains(resource))
                                     {
                                         return await isRootResourceSupported(resource, cancellationToken);
                                     }

                                     // Otherwise, make sure all dependencies are supported.
                                     return await resource.ListDependencies()
                                                          .ToAsyncEnumerable()
                                                          .AllAsync(async (dependency, cancellationToken) => await isResourceSupported(dependency, cancellationToken),
                                                                    cancellationToken);
                                 }))
                       .WithCancellation(cancellationToken);

        async ValueTask<bool> isRootResourceSupported(IResource resource, CancellationToken cancellationToken)
        {
            var uri = resource.GetCollectionUri(ParentChain.Empty, serviceUri);
            var result = await pipeline.GetContent(uri, cancellationToken);

            // A successful result means the resource is supported.
            // Otherwise, if the error indicates an unsupported SKU, the resource is not supported.
            // Any other error is unexpected and should be thrown.
            return result.Match(_ => true,
                                error => isUnsupportedSkuError(error)
                                         ? false
                                         : throw error.ToException());
        }

        static bool isUnsupportedSkuError(Error error) =>
                error.ToException() is HttpRequestException httpRequestException
                && httpRequestException.StatusCode switch
                {
                    HttpStatusCode.BadRequest => httpRequestException.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase),
                    HttpStatusCode.InternalServerError => httpRequestException.Message.Contains("Request processing failed due to internal error", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
    }

    public static void ConfigureListResourceNamesFromApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveListResourceNamesFromApim);
    }

    private static ListResourceNamesFromApim ResolveListResourceNamesFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (resource, parents, cancellationToken) =>
        {
            switch (resource)
            {
                case TagApiResource tagApiResource:
                    return ListTagApiNamesFromApim(parents, serviceUri, pipeline, cancellationToken);
                case TagProductResource tagProductResource:
                    return ListTagProductNamesFromApim(parents, serviceUri, pipeline, cancellationToken);
                default:
                    var uri = resource.GetCollectionUri(parents, serviceUri);

                    return pipeline.ListJsonObjects(uri, cancellationToken)
                                   .Select(jsonObject => from name in jsonObject.GetStringProperty("name")
                                                         from resourceName in ResourceName.From(name)
                                                         select resourceName)
                                   .Select(result => result.IfErrorThrow());
            }
        };
    }

    public static void ConfigureListResourceDtosFromApim(IHostApplicationBuilder builder)
    {
        ConfigureListResourceNamesFromApim(builder);
        ConfigureGetResourceDtoFromApim(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveListResourceDtosFromApim);
    }

    private static ListResourceDtosFromApim ResolveListResourceDtosFromApim(IServiceProvider provider)
    {
        var listNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var getDto = provider.GetRequiredService<GetResourceDtoFromApim>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (resource, parents, cancellationToken) =>
        {
            switch (resource)
            {
                case IPolicyResource policyResource:
                    return policyResource.ListDtosFromApim(parents, listNames, getDto, cancellationToken);
                case TagApiResource tagApiResource:
                    return ListTagApiDtosFromApim(parents, serviceUri, pipeline, cancellationToken);
                case TagProductResource tagProductResource:
                    return ListTagProductDtosFromApim(parents, serviceUri, pipeline, cancellationToken);
                case var _ when isWorkspaceResource(parents):
                    return getWorkspaceResourceDtos(resource, parents, cancellationToken);
                default:
                    return getNonWorkspaceResourceDtos(resource, parents, cancellationToken);
            }
        };

        bool isWorkspaceResource(ParentChain parents) =>
            parents.Any(parent => parent.Resource is WorkspaceResource);

        // The APIM REST API is buggy for some workspace-scoped resources.
        // If the resource is workspace-scoped, we fetch its DTO individually
        // rather than relying on the "list-all" response.
        IAsyncEnumerable<(ResourceName, JsonObject)> getWorkspaceResourceDtos(IResourceWithDto resource, ParentChain parents, CancellationToken cancellationToken)
        {
            var uri = resource.GetCollectionUri(parents, serviceUri);

            return pipeline.ListJsonObjects(uri, cancellationToken)
                           .Select(jsonObject => from nameString in jsonObject.GetStringProperty("name")
                                                 from name in ResourceName.From(nameString)
                                                 select name)
                           .Select(nameResult => nameResult.IfErrorThrow())
                           .Select(async (name, cancellationToken) => from dto in await resource.GetDtoFromApim(name, parents, serviceUri, pipeline, cancellationToken)
                                                                      select (name, dto))
                           .Select(result => result.IfErrorThrow());
        }

        IAsyncEnumerable<(ResourceName, JsonObject)> getNonWorkspaceResourceDtos(IResourceWithDto resource, ParentChain parents, CancellationToken cancellationToken)
        {
            var uri = resource.GetCollectionUri(parents, serviceUri);

            return pipeline.ListJsonObjects(uri, cancellationToken)
                           .Select(jsonObject => from nameString in jsonObject.GetStringProperty("name")
                                                 from name in ResourceName.From(nameString)
                                                 from dto in resource.DeserializeToDtoJson(jsonObject)
                                                 select (name, dto))
                           .Select(result => result.IfErrorThrow());
        }
    }

    /// <summary>
    /// The "list-all" API for policies does not support the raw XML format, so we need to fetch each policy individually.
    /// </summary>
    private static IAsyncEnumerable<(ResourceName, JsonObject)> ListDtosFromApim(this IPolicyResource resource, ParentChain parents, ListResourceNamesFromApim listNames, GetResourceDtoFromApim getDto, CancellationToken cancellationToken) =>
        listNames(resource, parents, cancellationToken)
            .Select(async (name, cancellationToken) =>
                    {
                        var dto = await getDto(resource, name, parents, cancellationToken);
                        return (name, dto);
                    });

    // Normalizes JSON structure by deserializing to DTO type and re-serializing back to JsonObject.
    // This validates the JSON conforms to the DTO schema and removes any extraneous fields.
    private static Result<JsonObject> DeserializeToDtoJson(this IResourceWithDto resource, JsonNode dto)
    {
        try
        {
            var deserializedDto = dto.Deserialize(resource.DtoType, resource.SerializerOptions);
            var newNode = JsonSerializer.SerializeToNode(deserializedDto, resource.DtoType, resource.SerializerOptions);

            return newNode.AsJsonObject();
        }
        catch (JsonException exception)
        {
            return Error.From(exception);
        }
    }

    private static Uri GetCollectionUri(this IResource resource, ParentChain parents, ServiceUri serviceUri) =>
        parents.GetUri(serviceUri)
               .AppendPathSegment(resource.CollectionUriPath)
               .ToUri();

    private static Uri GetUri(this ParentChain parents, ServiceUri serviceUri) =>
        parents.Aggregate(serviceUri.ToUri(),
                          (uri, parent) => uri.AppendPathSegments(parent.Resource.CollectionUriPath, parent.Name.ToString())
                                              .ToUri());

    public static void ConfigureDoesResourceExistInApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveDoesResourceExistInApim);
    }

    private static DoesResourceExistInApim ResolveDoesResourceExistInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return async (resourceKey, cancellationToken) =>
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            switch (resource)
            {
                case ILinkResource linkResource:
                    return await linkResource.ExistsInApim(name, parents, serviceUri, pipeline, cancellationToken);
                case TagApiResource tagApiResource:
                    return await TagApiExistsInApim(name, parents, serviceUri, pipeline, cancellationToken);
                case TagProductResource tagProductResource:
                    return await TagProductExistsInApim(name, parents, serviceUri, pipeline, cancellationToken);
                default:
                    var uri = resource.GetUri(name, parents, serviceUri);
                    var result = await pipeline.Head(uri, cancellationToken);

                    return result.IfErrorThrow()
                                 .IsSome;
            }
        };
    }

    /// <summary>
    /// For link resources, check for existence by doing a GET and inspecting the result for a 404.
    /// </summary>
    private static async ValueTask<bool> ExistsInApim(this ILinkResource resource, ResourceName name, ParentChain parents, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var uri = resource.GetUri(name, parents, serviceUri);
        var result = await pipeline.GetOptionalContent(uri, cancellationToken);

        return result.IfErrorThrow()
                     .IsSome;
    }

    public static Uri GetUri(this IResource resource, ResourceName name, ParentChain parents, ServiceUri serviceUri) =>
        resource.GetCollectionUri(parents, serviceUri)
                .AppendPathSegment(name)
                .ToUri();

    public static void ConfigurePutResourceInApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ConfigureDoesResourceExistInApim(builder);
        ConfigureGetResourceDtoFromApim(builder);
        ConfigureListResourceNamesFromApim(builder);
        ConfigureDeleteResourceFromApim(builder);
        ConfigureIsResourceSupportedInApim(builder);

        builder.TryAddSingleton(ResolvePutResourceInApim);
    }

    private static PutResourceInApim ResolvePutResourceInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var getApimDto = provider.GetRequiredService<GetResourceDtoFromApim>();
        var doesResourceExist = provider.GetRequiredService<DoesResourceExistInApim>();
        var listNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var deleteResource = provider.GetRequiredService<DeleteResourceFromApim>();
        var isResourceSupported = provider.GetRequiredService<IsResourceSupportedInApim>();

        return async (resource, name, dto, parents, cancellationToken) =>
        {
            var resourceKey = ResourceKey.From(resource, name, parents);

            switch (resource)
            {
                case ApiResource:
                    await PutApiInApim(name, dto, getApimDto, pipeline, serviceUri, cancellationToken);
                    return;
                case WorkspaceApiResource:
                    await PutWorkspaceApiInApim(name, dto, parents, getApimDto, pipeline, serviceUri, cancellationToken);
                    return;
                case ProductResource:
                    await PutProductInApim(name, dto, pipeline, serviceUri, isResourceSupported, doesResourceExist, listNames, deleteResource, cancellationToken);
                    return;
                case WorkspaceProductResource:
                    await PutWorkspaceProductInApim(name, dto, parents, pipeline, serviceUri, isResourceSupported, doesResourceExist, listNames, deleteResource, cancellationToken);
                    return;
                case ApiReleaseResource:
                    await PutApiReleaseInApim(name, dto, parents, pipeline, serviceUri, cancellationToken);
                    return;
                case WorkspaceApiReleaseResource:
                    await PutWorkspaceApiReleaseInApim(name, dto, parents, pipeline, serviceUri, cancellationToken);
                    return;
                case TagApiResource:
                    await PutTagApiInApim(name, dto, parents, pipeline, serviceUri, cancellationToken);
                    return;
                case TagProductResource:
                    await PutTagProductInApim(name, dto, parents, pipeline, serviceUri, cancellationToken);
                    return;
                default:
                    var uri = resource.GetUri(name, parents, serviceUri);
                    var result = await pipeline.PutJson(uri, dto, cancellationToken);
                    result.IfErrorThrow();
                    return;
            }
        };
    }

    public static void ConfigureDeleteResourceFromApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveDeleteResourceFromApim);
    }

    private static DeleteResourceFromApim ResolveDeleteResourceFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return async (resourceKey, ignoreNotFound, waitForCompletion, cancellationToken) =>
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            switch (resource)
            {
                case TagApiResource tagApiResource:
                    await DeleteTagApiFromApim(name, parents, pipeline, serviceUri, ignoreNotFound, waitForCompletion, cancellationToken);
                    return;
                case TagProductResource tagProductResource:
                    await DeleteTagProductFromApim(name, parents, pipeline, serviceUri, ignoreNotFound, waitForCompletion, cancellationToken);
                    return;
                default:
                    var uri = resource.GetUri(name, parents, serviceUri);
                    var result = await pipeline.Delete(uri, cancellationToken, ignoreNotFound, waitForCompletion);
                    result.IfErrorThrow();
                    return;
            }
        };
    }

    public static void ConfigureGetOptionalResourceDtoFromApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveGetOptionalResourceDtoFromApim);
    }

    private static GetOptionalResourceDtoFromApim ResolveGetOptionalResourceDtoFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return async (resource, name, parents, cancellationToken) =>
        {
            var result = await resource.GetDtoFromApim(name, parents, serviceUri, pipeline, cancellationToken);

            return result.Match(Option.Some,
                                error => error.ToException() switch
                                {
                                    HttpRequestException httpRequestException when httpRequestException.StatusCode == HttpStatusCode.NotFound => Option.None,
                                    var exception => throw exception
                                });
        };
    }

    private static async ValueTask<Result<JsonObject>> GetDtoFromApim(this IResourceWithDto resource, ResourceName name, ParentChain parents, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        if (resource is ICompositeResource and not ILinkResource)
        {
            return new JsonObject();
        }

        var uri = resource.GetUri(name, parents, serviceUri);

        uri = resource is IPolicyResource
                ? uri.AppendQueryParam("format", "rawxml")
                     .ToUri()
                : uri;

        return from content in await pipeline.GetContent(uri, cancellationToken)
               from json in JsonObjectModule.From(content, resource.SerializerOptions)
               from formattedJson in resource.DeserializeToDtoJson(json)
               select formattedJson;
    }

    public static void ConfigureGetResourceDtoFromApim(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(ResolveGetResourceDtoFromApim);
    }

    private static GetResourceDtoFromApim ResolveGetResourceDtoFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return async (resource, name, parents, cancellationToken) =>
        {
            var result = await resource.GetDtoFromApim(name, parents, serviceUri, pipeline, cancellationToken);

            return result.IfErrorThrow();
        };
    }
}