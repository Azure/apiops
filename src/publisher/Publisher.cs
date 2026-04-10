using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask RunPublisher(CancellationToken cancellationToken);

internal static class PublisherModule
{
    public static void ConfigureRunPublisher(IHostApplicationBuilder builder)
    {
        RelationshipsModule.ConfigureGetCurrentRelationships(builder);
        RelationshipsModule.ConfigureGetPreviousRelationships(builder);
        ResourceModule.ConfigureListResourcesToProcess(builder);
        ResourceModule.ConfigureIsResourceInFileSystem(builder);
        ResourceModule.ConfigurePutResource(builder);
        ResourceModule.ConfigureDeleteResource(builder);

        builder.TryAddSingleton(ResolveRunPublisher);
    }

    internal static RunPublisher ResolveRunPublisher(IServiceProvider provider)
    {
        var getCurrentRelationships = provider.GetRequiredService<GetCurrentRelationships>();
        var getPreviousRelationships = provider.GetRequiredService<GetPreviousRelationships>();
        var listResourcesToProcess = provider.GetRequiredService<ListResourcesToProcess>();
        var isInFileSystem = provider.GetRequiredService<IsResourceInFileSystem>();
        var putResource = provider.GetRequiredService<PutResource>();
        var deleteResource = provider.GetRequiredService<DeleteResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var cache = new ConcurrentDictionary<ResourceKey, Lazy<Task>>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("run.publisher");

            logger.LogInformation("Running publisher...");

            var resourcesToProcess = await listResourcesToProcess(cancellationToken);
            var currentRelationships = await getCurrentRelationships(cancellationToken);
            var previousRelationships = await getPreviousRelationships(cancellationToken);

            await processResources(resourcesToProcess, currentRelationships, previousRelationships, cancellationToken);

            logger.LogInformation("Publisher completed successfully.");
        };

        async ValueTask processResources(ImmutableHashSet<ResourceKey> resources, Relationships currentRelationships, Relationships previousRelationships, CancellationToken cancellationToken) =>
            await resources.IterTaskParallel(async resource => await processResource(resource, resources, currentRelationships, previousRelationships, cancellationToken),
                                             maxDegreeOfParallelism: Option.None,
                                             cancellationToken);

        async ValueTask processResource(ResourceKey resourceKey, ImmutableHashSet<ResourceKey> resourceSet, Relationships currentRelationships, Relationships previousRelationships, CancellationToken cancellationToken)
        {
            await cache.GetOrAdd(resourceKey, _ => new Lazy<Task>(processInner))
                       .Value;

            async Task processInner()
            {

                if (await isInFileSystem(resourceKey, cancellationToken))
                {
                    await processPut();
                }
                else
                {
                    await processDelete();
                }
            }

            async ValueTask processPut()
            {
                // Process predecessors first
                var predecessors = currentRelationships.Predecessors
                                                       .Find(resourceKey)
                                                       .IfNone(() => []);

                await predecessors.IterTaskParallel(async predecessor => await processResource(predecessor, resourceSet, currentRelationships, previousRelationships, cancellationToken),
                                                    maxDegreeOfParallelism: Option.None,
                                                    cancellationToken);

                // Put the resource
                if (resourceSet.Contains(resourceKey))
                {
                    await putResource(resourceKey, cancellationToken);
                }
            }

            async ValueTask processDelete()
            {
                // Skip if not actually being deleted this run (e.g. WorkspaceResource has no artifact file).
                // Cascading to successors here would deadlock with their processPut tasks.
                if (resourceSet.Contains(resourceKey) is false)
                {
                    return;
                }

                // Process successors first
                var successors = previousRelationships.Successors
                                                      .Find(resourceKey)
                                                      .IfNone(() => []);

                await successors.IterTaskParallel(async successor => await processResource(successor, resourceSet, currentRelationships, previousRelationships, cancellationToken),
                                                  maxDegreeOfParallelism: Option.None,
                                                  cancellationToken);

                // Delete resource
                await deleteResource(resourceKey, cancellationToken);
            }
        }
    }
}