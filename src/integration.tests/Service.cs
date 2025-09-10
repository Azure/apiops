using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask EmptyService(CancellationToken cancellationToken);
internal delegate ValueTask PopulateService(ResourceModels models, CancellationToken cancellationToken);
internal delegate ValueTask<bool> IsSkuSupported(IResource resource, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask<bool> ShouldSkipResource(IResource resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken);

internal static class ServiceModule
{
    public static void ConfigureEmptyService(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureHttpPipeline(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);
        ResourceGraphModule.ConfigureBuilder(builder);
        ConfigureIsSkuSupported(builder);
        ConfigureShouldSkipResource(builder);

        builder.TryAddSingleton(GetEmptyService);
    }

    private static EmptyService GetEmptyService(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isSkuSupported = provider.GetRequiredService<IsSkuSupported>();
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var rootResources = graph.GetTraversalRootResources();
        var deleteTimeout = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(90)).Build();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("empty.service");

            var tasks = new ConcurrentDictionary<IResource, Lazy<Task>>();

            await rootResources.IterTaskParallel(async resource => await emptyResource(resource, tasks, cancellationToken),
                                                 maxDegreeOfParallelism: Option.None,
                                                 cancellationToken);
        };

        async ValueTask emptyResource(IResource resource, ConcurrentDictionary<IResource, Lazy<Task>> tasks, CancellationToken cancellationToken) =>
            await tasks.GetOrAdd(resource, _ => new(async () =>
            {
                var ancestors = ResourceAncestors.Empty;

                // Skip unsupported resources
                if (await isSkuSupported(resource, ancestors, cancellationToken) is false)
                {
                    return;
                }

                // Delete dependents
                await getDependents(resource)
                          .IterTaskParallel(async dependent => await emptyResource(dependent, tasks, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);

                switch (resource)
                {
                    case ApiResource:
                        // Delete all non-current revisions first, then the current revision
                        var nonCurrentRevisions = new List<(IResource, ResourceName, ResourceAncestors)>();
                        var currentRevisions = new List<(IResource, ResourceName, ResourceAncestors)>();

                        await resource.ListNames(ancestors, serviceUri, pipeline, cancellationToken)
                                      .IterTask(async name =>
                                      {
                                          await ValueTask.CompletedTask;

                                          if (ApiRevisionModule.IsRootName(name))
                                          {
                                              currentRevisions.Add((resource, name, ancestors));
                                          }
                                          else
                                          {
                                              nonCurrentRevisions.Add((resource, name, ancestors));
                                          }
                                      }, cancellationToken);

                        await bulkDelete(nonCurrentRevisions.ToAsyncEnumerable(), cancellationToken);
                        await bulkDelete(currentRevisions.ToAsyncEnumerable(), cancellationToken);

                        break;
                    default:
                        var resources = resource.ListNames(ancestors, serviceUri, pipeline, cancellationToken)
                                                .Select(name => (resource, name, ancestors));

                        await bulkDelete(resources, cancellationToken);

                        break;
                }
            })).Value;

        // Finds all root resources that must be deleted before the given resource.
        IEnumerable<IResource> getDependents(IResource resource)
        {
            var dependents = new HashSet<IResource>();

            graph.TopologicallySortedResources
                 .Where(potentialReferencer => potentialReferencer is IResourceWithReference resourceWithReference
                                                && (resourceWithReference.MandatoryReferencedResourceDtoProperties.ContainsKey(resource)
                                                    || resourceWithReference.OptionalReferencedResourceDtoProperties.ContainsKey(resource)))
                 .Iter(addTraversalChain);

            return dependents.Where(rootResources.Contains);

            void addTraversalChain(IResource resource)
            {
                dependents.Add(resource);

                resource.GetTraversalPredecessor()
                        .Iter(addTraversalChain);
            }
        }

        async ValueTask bulkDelete(IAsyncEnumerable<(IResource resource, ResourceName name, ResourceAncestors ancestors)> resources, CancellationToken cancellationToken) =>
            await resources.Where(async (x, cancellationToken) => await shouldSkipResource(x.resource, x.name, x.ancestors, cancellationToken) is false)
                           .Chunk(50)
                           .IterTask(async chunk =>
                           {
                               var uri = new Uri("https://management.azure.com/batch?api-version=2022-12-01");

                               var content = new JsonObject
                               {
                                   ["requests"] = chunk.Select(x =>
                                   {
                                       var (resource, name, ancestors) = x;

                                       return new JsonObject()
                                       {
                                           ["name"] = Guid.NewGuid().ToString(),
                                           ["httpMethod"] = "DELETE",
                                           ["url"] = resource.GetUri(name, ancestors, serviceUri).ToString()
                                       };
                                   }).ToJsonArray()
                               };

                               await pipeline.PostJson(uri, content, cancellationToken);
                           }, cancellationToken);
    }

    public static void ConfigurePopulateService(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureHttpPipeline(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);
        ConfigureIsSkuSupported(builder);

        builder.TryAddSingleton(GetPopulateService);
    }

    private static PopulateService GetPopulateService(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var isSkuSupported = provider.GetRequiredService<IsSkuSupported>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (models, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("populate.service");

            var tasks = new ConcurrentDictionary<ModelNode, Lazy<Task>>();

            await models.SelectMany(kvp => kvp.Value)
                        .IterTaskParallel(async node => await putModel(node, models, tasks, cancellationToken),
                                          maxDegreeOfParallelism: Option.None,
                                          cancellationToken);
        };

        async ValueTask putModel(ModelNode node, ResourceModels resourceModels, ConcurrentDictionary<ModelNode, Lazy<Task>> tasks, CancellationToken cancellationToken) =>
            await tasks.GetOrAdd(node, _ => new Lazy<Task>(async () =>
            {
                // Skip non-DTO models
                if (node.Model.AssociatedResource is not IResourceWithDto resource
                    || node.Model is not IDtoTestModel model)
                {
                    return;
                }

                // Skip unsupported resources
                var ancestors = node.GetResourceAncestors();
                if (await isSkuSupported(resource, ancestors, cancellationToken) is false)
                {
                    return;
                }

                // Put predecessors
                await node.Predecessors
                          .IterTaskParallel(async predecessor => await putModel(predecessor, resourceModels, tasks, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);

                // If this is not the current API revision, put the current API revision first
                if (model is ApiModel apiModel && ApiRevisionModule.IsRootName(apiModel.Name) is false)
                {
                    var currentRevisionNode = resourceModels.Find(ApiResource.Instance)
                                                            .IfNone(() => ModelNodeSet.Empty)
                                                            .Single(node => node.Model.Name == ApiRevisionModule.GetRootName(apiModel.Name));

                    await putModel(currentRevisionNode, resourceModels, tasks, cancellationToken);
                }

                // Put model
                var name = model.Name;
                var dto = model.SerializeDto(node.Predecessors);

                logger.LogInformation("Putting {Resource} '{Name}'{Ancestors}.", resource.SingularName, name, ancestors.ToLogString());
                await resource.PutDto(name, dto, ancestors, serviceUri, pipeline, cancellationToken);
            })).Value;
    }

    public static void ConfigureIsSkuSupported(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureHttpPipeline(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);
        builder.TryAddSingleton(GetIsSkuSupported);
    }

    private static IsSkuSupported GetIsSkuSupported(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();

        return async (resource, ancestors, cancellationToken) =>
            await resource.IsSkuSupported(ancestors, serviceUri, pipeline, cancellationToken);
    }

    public static void ConfigureShouldSkipResource(IHostApplicationBuilder builder)
    {
        ConfigureIsSkuSupported(builder);
        builder.TryAddSingleton(GetShouldSkipResource);
    }

    private static ShouldSkipResource GetShouldSkipResource(IServiceProvider provider)
    {
        var isSkuSupported = provider.GetRequiredService<IsSkuSupported>();

        return async (resource, name, ancestors, cancellationToken) =>
            // Skip unsupported resources
            await isSkuSupported(resource, ancestors, cancellationToken) is false
            // Skip system groups
            || (resource is GroupResource
                && (name == GroupResource.Administrators || name == GroupResource.Developers || name == GroupResource.Guests))
            // Skip master subscription
            || (resource is SubscriptionResource && name == SubscriptionResource.Master);
    }
}