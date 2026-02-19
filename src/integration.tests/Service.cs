using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask EmptyService(CancellationToken cancellationToken);
internal delegate ValueTask PopulateService(ResourceModels models, CancellationToken cancellationToken);
internal delegate ValueTask<bool> ShouldSkipResource(ResourceKey resourceKey, CancellationToken cancellationToken);

internal static class ServiceModule
{
    private static readonly Lazy<ImmutableHashSet<IResource>> testResources = new(ListTestResources);

    private static ImmutableHashSet<IResource> ListTestResources()
    {
        return [.. from type in typeof(ITestModel<>).Assembly.GetTypes()
                   where isTestModel(type)
                   select getResource(type) ];

        static bool isTestModel(Type type) =>
            type.IsClass
            && type.IsAbstract is false
            && type.GetInterfaces()
                   .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITestModel<>));

        static IResource getResource(Type type)
        {
            var propertyName = nameof(ITestModel<>.AssociatedResource);
            var property = type.GetProperty(propertyName,
                                            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                           ?? throw new InvalidOperationException($"Type {type.FullName} must expose a public static {propertyName} property.");

            if (property.GetValue(null) is not IResource resource)
            {
                throw new InvalidOperationException($"Type {type.FullName}'s property {propertyName} must implement {nameof(IResource)}.");
            }

            return resource;
        }
    }

    public static void ConfigureEmptyService(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);
        ResourceModule.ConfigureListResourceNamesFromApim(builder);
        ConfigureShouldSkipResource(builder);
        AzureModule.ConfigureAzureEnvironment(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);

        builder.TryAddSingleton(ResolveEmptyService);
    }

    private static EmptyService ResolveEmptyService(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isSkuSupported = provider.GetRequiredService<IsResourceSupportedInApim>();
        var listNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var rootResources = graph.ListTraversalRootResources();
        var parents = ParentChain.Empty;

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("empty.service");

            var tasks = new ConcurrentDictionary<IResource, Lazy<Task>>();

            await rootResources.IterTaskParallel(async resource => await emptyResource(resource, tasks, cancellationToken),
                                                 maxDegreeOfParallelism: Option.None,
                                                 cancellationToken);
        };

        async ValueTask emptyResource(IResource resource, ConcurrentDictionary<IResource, Lazy<Task>> tasks, CancellationToken cancellationToken)
        {
            await tasks.GetOrAdd(resource, _ => new(emptyResourceInner))
                       .Value;

            async Task emptyResourceInner()
            {
                // Skip unsupported resources
                if (await isSkuSupported(resource, cancellationToken) is false)
                {
                    return;
                }

                // Delete dependents
                await getDependentRootResources(resource)
                          .IterTaskParallel(async dependent => await emptyResource(dependent, tasks, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);

                switch (resource)
                {
                    case ApiResource:
                        // Delete all non-current revisions first, then the current revision
                        var (currentRevisions, nonCurrentRevisions) =
                            await listNames(resource, parents, cancellationToken)
                                    .Select(name => ResourceKey.From(resource, name, parents))
                                    .Partition(resourceKey => ApiRevisionModule.IsRootName(resourceKey.Name), cancellationToken);

                        await bulkDelete(nonCurrentRevisions.ToAsyncEnumerable(), cancellationToken);
                        await bulkDelete(currentRevisions.ToAsyncEnumerable(), cancellationToken);

                        break;
                    default:
                        var resources = listNames(resource, parents, cancellationToken)
                                            .Select(name => ResourceKey.From(resource, name, parents));

                        await bulkDelete(resources, cancellationToken);

                        break;
                }
            }
        }

        IEnumerable<IResource> getDependentRootResources(IResource resource)
        {
            var successors = graph.ListDependents(resource);
            var successorParents = from successor in successors
                                   select getParentRootResource(successor);

            var successorDependentRoots = from successor in successors
                                          from dependentRootResource in getDependentRootResources(successor)
                                          select dependentRootResource;

            return from root in successorParents.Union(successorDependentRoots)
                   where root != resource
                   where rootResources.Contains(root)
                   select root;
        }

        static IResource getParentRootResource(IResource resource) =>
            resource.GetTraversalPredecessorHierarchy() switch
            {
                [var root, ..] => root,
                [] => resource
            };

        async ValueTask bulkDelete(IAsyncEnumerable<ResourceKey> resources, CancellationToken cancellationToken) =>
            await resources// Don't delete resources that should be skipped
                           .Where(async (resourceKey, cancellationToken) => await shouldSkipResource(resourceKey, cancellationToken) is false)
                           // Don't delete resources that don't have test models
                           .Where(resourceKey => testResources.Value.Contains(resourceKey.Resource))
                           .Chunk(50)
                           .IterTask(async chunk =>
                           {
                               var uri = new Uri($"{azureEnvironment.ManagementEndpoint}/batch?api-version=2022-12-01");

                               var content = new JsonObject
                               {
                                   ["requests"] = chunk.Select(x => new JsonObject()
                                   {
                                       ["name"] = Guid.NewGuid().ToString(),
                                       ["httpMethod"] = "DELETE",
                                       ["url"] = x.Resource.GetUri(x.Name, x.Parents, serviceUri).ToString()
                                   }).ToJsonArray()
                               };

                               await pipeline.PostJson(uri, content, cancellationToken);
                           }, cancellationToken);
    }

    public static void ConfigurePopulateService(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);
        ResourceModule.ConfigurePutResourceInApim(builder);
        ResourceModule.ConfigurePutApiSpecificationInApim(builder);

        builder.TryAddSingleton(ResolvePopulateService);
    }

    private static PopulateService ResolvePopulateService(IServiceProvider provider)
    {
        var isSkuSupported = provider.GetRequiredService<IsResourceSupportedInApim>();
        var putInApim = provider.GetRequiredService<PutResourceInApim>();
        var putApiSpecification = provider.GetRequiredService<PutApiSpecificationInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (models, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("populate.service");

            var tasks = new ConcurrentDictionary<ModelNode, Lazy<Task>>();

            await models.SelectMany(kvp => kvp.Value)
                        .IterTaskParallel(async node => await putModel(node, models, tasks, cancellationToken),
                                          maxDegreeOfParallelism: Option.None,
                                          cancellationToken);
        };

        async ValueTask putModel(ModelNode node, ResourceModels resourceModels, ConcurrentDictionary<ModelNode, Lazy<Task>> tasks, CancellationToken cancellationToken)
        {
            await tasks.GetOrAdd(node, _ => new Lazy<Task>(putModelInner))
                       .Value;

            async Task putModelInner()
            {
                // Skip non-DTO models
                if (node.Model.AssociatedResource is not IResourceWithDto resource
                    || node.Model is not IDtoTestModel model)
                {
                    return;
                }

                // Skip unsupported resources
                var parents = node.GetResourceParentChain();
                if (await isSkuSupported(resource, cancellationToken) is false)
                {
                    return;
                }

                // Put predecessors
                await node.Predecessors
                          .IterTaskParallel(async predecessor => await putModel(predecessor, resourceModels, tasks, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);

                var name = model.Name;
                switch (model)
                {
                    case ApiModel apiModel:
                        {
                            // If this is not the current API revision, put the current API revision first
                            if (ApiRevisionModule.IsRootName(name) is false)
                            {
                                var currentRevisionNode = resourceModels.Find(ApiResource.Instance)
                                                                        .IfNone(() => ModelNodeSet.Empty)
                                                                        .Single(node => node.Model.Name == ApiRevisionModule.GetRootName(name));

                                await putModel(currentRevisionNode, resourceModels, tasks, cancellationToken);
                            }

                            // Put model
                            var dto = model.SerializeDto(node.Predecessors);
                            await putInApim(resource, name, dto, parents, cancellationToken);

                            // Put API specification
                            var resourceKey = ResourceKey.From(resource, name, parents);

                            var option = from specification in apiModel.Type switch
                            {
                                ApiType.Http => Option.Some<ApiSpecification>(new ApiSpecification.OpenApi
                                {
                                    Format = OpenApiFormat.Yaml.Instance,
                                    Version = OpenApiVersion.V3.Instance,
                                }),
                                ApiType.Soap => ApiSpecification.Wsdl.Instance,
                                ApiType.GraphQl => ApiSpecification.GraphQl.Instance,
                                _ => Option.None
                            }
                                         from contentsString in apiModel.Specification
                                         let contents = BinaryData.FromString(contentsString)
                                         select (specification, contents);

                            await option.IterTask(async tuple => await putApiSpecification(resourceKey, tuple.specification, tuple.contents, cancellationToken));

                            break;
                        }
                    default:
                        {
                            var dto = model.SerializeDto(node.Predecessors);
                            await putInApim(resource, name, dto, parents, cancellationToken);
                            break;
                        }
                }
            }
        }
    }

    public static void ConfigureShouldSkipResource(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);

        builder.TryAddSingleton(ResolveShouldSkipResource);
    }

    private static ShouldSkipResource ResolveShouldSkipResource(IServiceProvider provider)
    {
        var isSkuSupported = provider.GetRequiredService<IsResourceSupportedInApim>();

        return async (resourceKey, cancellationToken) =>
        {
            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            // Skip unsupported resources
            return await isSkuSupported(resource, cancellationToken) is false
            // Skip system groups
            || (resource is GroupResource
                && (name == GroupResource.Administrators || name == GroupResource.Developers || name == GroupResource.Guests))
            // Skip master subscription
            || (resource is SubscriptionResource && name == SubscriptionResource.Master);
        };
    }
}

file static class Extensions
{
    public static async ValueTask<(ImmutableArray<T> Matches, ImmutableArray<T> NonMatches)> Partition<T>(this IAsyncEnumerable<T> source, Func<T, bool> predicate, CancellationToken cancellationToken)
    {
        var matches = new List<T>();
        var nonMatches = new List<T>();

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            if (predicate(item))
            {
                matches.Add(item);
            }
            else
            {
                nonMatches.Add(item);
            }
        }

        return ([.. matches], [.. nonMatches]);
    }
}