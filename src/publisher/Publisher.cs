using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask RunPublisher(CancellationToken cancellationToken);

internal static class PublisherModule
{
    public static void ConfigureRunPublisher(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureListResourcesToPut(builder);
        ResourceModule.ConfigureListResourcesToDelete(builder);
        ResourceModule.ConfigurePutResource(builder);
        ResourceModule.ConfigureDeleteResource(builder);
        ResourceGraphModule.ConfigureBuilder(builder);

        builder.TryAddSingleton(GetRunPublisher);
    }

    private static RunPublisher GetRunPublisher(IServiceProvider provider)
    {
        var listResourcesToPut = provider.GetRequiredService<ListResourcesToPut>();
        var listResourcesToDelete = provider.GetRequiredService<ListResourcesToDelete>();
        var graph = provider.GetRequiredService<ResourceGraph>();
        var putResource = provider.GetRequiredService<PutResource>();
        var deleteResource = provider.GetRequiredService<DeleteResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("run.publisher");

            logger.LogInformation("Running publisher...");

            // Get dictionary for topologically sorting resources
            var resourceOrderDictionary = graph.TopologicallySortedResources
                                               .Select((resource, index) => (resource, index))
                                               .ToImmutableDictionary(pair => pair.resource, pair => pair.index);

            await listResourcesToPut(cancellationToken)
                    .GroupBy(x => x.resource)
                    // Put resources in topological order   
                    .OrderBy(group => resourceOrderDictionary[group.Key])
                    .IterTask(async group => await (group.Key switch
                    {
                        ApiResource => putApis([.. group], cancellationToken),
                        _ => putResources(group, cancellationToken)
                    }), cancellationToken);

            await listResourcesToDelete(cancellationToken)
                    .GroupBy(x => x.resource)
                    // Delete resources in reverse topological order
                    .OrderByDescending(group => resourceOrderDictionary[group.Key])
                    .IterTask(async group => await (group.Key switch
                    {
                        ApiResource => deleteApis([.. group], cancellationToken),
                        _ => deleteResources(group, cancellationToken)
                    }), cancellationToken);

            logger.LogInformation("Publisher completed successfully.");
        };

        async ValueTask putApis(ICollection<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)> apis, CancellationToken cancellationToken)
        {
            var currentRevisions = new List<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)>();
            var nonCurrentRevisions = new List<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)>();

            apis.Iter(api =>
            {
                if (ApiRevisionModule.IsRootName(api.Name))
                {
                    currentRevisions.Add(api);
                }
                else
                {
                    nonCurrentRevisions.Add(api);
                }
            }, cancellationToken);

            await putResources(currentRevisions, cancellationToken);
            await putResources(nonCurrentRevisions, cancellationToken);
        }

        async ValueTask putResources(IEnumerable<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)> resources, CancellationToken cancellationToken) =>
            await resources.IterTaskParallel(async resource => await putResource(resource.Resource, resource.Name, resource.Ancestors, cancellationToken),
                                             maxDegreeOfParallelism: Option.None,
                                             cancellationToken);

        async ValueTask deleteApis(ICollection<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)> apis, CancellationToken cancellationToken)
        {
            var currentRevisions = new List<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)>();
            var nonCurrentRevisions = new List<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)>();

            apis.Iter(api =>
            {
                if (ApiRevisionModule.IsRootName(api.Name))
                {
                    currentRevisions.Add(api);
                }
                else
                {
                    nonCurrentRevisions.Add(api);
                }
            }, cancellationToken);

            await deleteResources(nonCurrentRevisions, cancellationToken);
            await deleteResources(currentRevisions, cancellationToken);
        }

        async ValueTask deleteResources(IEnumerable<(IResourceWithDto Resource, ResourceName Name, ResourceAncestors Ancestors)> resources, CancellationToken cancellationToken) =>
            await resources.IterTaskParallel(async resource => await deleteResource(resource.Resource, resource.Name, resource.Ancestors, cancellationToken),
                                             maxDegreeOfParallelism: Option.None,
                                             cancellationToken);
    }
}