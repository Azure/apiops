using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask WipeApim(CancellationToken cancellationToken);
internal delegate ValueTask PopulateApim(TestState testState, CancellationToken cancellationToken);
internal delegate ValueTask<bool> IsResourceKeySupported(ResourceKey resourceKey, CancellationToken cancellationToken);

internal static class ApimModule
{
    public static void ConfigureWipeApim(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);
        ConfigureIsResourceKeySupported(builder);
        ResourceModule.ConfigureListResourceNamesFromApim(builder);
        ResourceModule.ConfigureDeleteResourceFromApim(builder);

        builder.TryAddSingleton(ResolveWipeApim);
    }

    private static WipeApim ResolveWipeApim(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isResourceSupported = provider.GetRequiredService<IsResourceSupportedInApim>();
        var isResourceKeySupported = provider.GetRequiredService<IsResourceKeySupported>();
        var listNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var deleteResource = provider.GetRequiredService<DeleteResourceFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var testResources = TestsModule.Resources;
        var rootResources = graph.ListTraversalRootResources()
                                 .Where(testResources.Contains)
                                 .ToImmutableHashSet();

        return async cancellationToken =>
        {
            using var activity = activitySource.StartActivity("apim.wipe");

            var tasks = new ConcurrentDictionary<IResource, Lazy<Task>>();

            await rootResources.IterTaskParallel(async resource => await deleteAllInstances(resource, tasks, cancellationToken),
                                                 maxDegreeOfParallelism: Option.None,
                                                 cancellationToken);
        };

        async ValueTask deleteAllInstances(IResource resource, ConcurrentDictionary<IResource, Lazy<Task>> tasks, CancellationToken cancellationToken)
        {
            await tasks.GetOrAdd(resource, _ => new(async () =>
            {
                // Delete dependent roots first
                await getDependentRoots(resource)
                        .IterTaskParallel(async root => await deleteAllInstances(root, tasks, cancellationToken),
                                          maxDegreeOfParallelism: Option.None,
                                          cancellationToken);

                // Skip if not supported
                if (await isResourceSupported(resource, cancellationToken) is false)
                {
                    return;
                }

                // List and delete all instances of this resource type
                var parentChain = ParentChain.Empty;
                var names = listNames(resource, parentChain, cancellationToken);

                await (resource switch
                {
                    ApiResource =>
                        names.GroupBy(name => ApiRevisionModule.IsRootName(name)
                                                ? 1
                                                : 0)
                             .OrderBy(group => group.Key)
                             .IterTask(async group => await deleteNames(resource, group.ToAsyncEnumerable(), parentChain, cancellationToken), cancellationToken),
                    _ =>
                        deleteNames(resource, names, parentChain, cancellationToken)
                });
            })).Value;
        }

        ImmutableHashSet<IResource> getDependentRoots(IResource resource)
        {
            return [.. getDependentPool(resource)
                        .Choose(getResourceRoot)
                        .Where(root => root != resource)
                       ];

            IEnumerable<IResource> getDependentPool(IResource resource) =>
                from dependent in graph.ListDependents(resource)
                from descendent in getDependentPool(dependent).Prepend(dependent)
                select descendent;

            Option<IResource> getResourceRoot(IResource resource) =>
                resource.GetTraversalPredecessorHierarchy()
                        .Head()
                        .Where(rootResources.Contains)
                        .IfNone(() => rootResources.Contains(resource)
                                      ? Option.Some(resource)
                                      : Option.None);
        }

        async ValueTask deleteNames(IResource resource, IAsyncEnumerable<ResourceName> names, ParentChain parents, CancellationToken cancellationToken) =>
            await names.Select(name => ResourceKey.From(resource, name, parents))
                       .Where(async (key, cancellationToken) => await isResourceKeySupported(key, cancellationToken))
                       .IterTaskParallel(async key => await deleteResource(key, ignoreNotFound: true, waitForCompletion: true, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);
    }

    public static void ConfigureIsResourceKeySupported(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);

        builder.TryAddSingleton(ResolveIsResourceKeySupported);
    }

    private static IsResourceKeySupported ResolveIsResourceKeySupported(IServiceProvider provider)
    {
        var isResourceSupported = provider.GetRequiredService<IsResourceSupportedInApim>();

        return async (resourceKey, cancellationToken) =>
        {
            var (resource, name) = (resourceKey.Resource, resourceKey.Name);

            if (TestsModule.Resources.Contains(resource) is false)
            {
                return false;
            }

            if (await isResourceSupported(resource, cancellationToken) is false)
            {
                return false;
            }

            return resource switch
            {
                // Skip system groups
                GroupResource when name == GroupResource.Administrators => false,
                GroupResource when name == GroupResource.Developers => false,
                GroupResource when name == GroupResource.Guests => false,
                // Skip master subscription
                SubscriptionResource when name == SubscriptionResource.Master => false,
                _ => true
            };
        };
    }

    public static void ConfigurePopulateApim(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureSortResources(builder);
        ResourceModule.ConfigurePutResourceInApim(builder);
        ConfigureIsResourceKeySupported(builder);

        builder.TryAddSingleton(ResolvePopulateApim);
    }

    private static PopulateApim ResolvePopulateApim(IServiceProvider provider)
    {
        var sortResources = provider.GetRequiredService<SortResources>();
        var putResource = provider.GetRequiredService<PutResourceInApim>();
        var isResourceKeySupported = provider.GetRequiredService<IsResourceKeySupported>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (testState, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("apim.populate");
            activity?.SetTag("test.state", testState);

            var sortedResources = sortResources(testState.Models
                                                         .Select(model => model.Key.Resource)
                                                         .Distinct());
                                                         
            var topologicalOrder = sortedResources.Select((resource, index) => (resource, index))
                                                  .ToImmutableDictionary(x => x.resource, x => x.index);

            await testState.Models
                           .GroupBy(model => model.Key.Resource)
                           .OrderBy(group => topologicalOrder[group.Key])
                           .IterTask(async group => await putResourceGroup(group, cancellationToken), cancellationToken);
        };

        async ValueTask putResourceGroup(IGrouping<IResource, ITestModel> group, CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity("apim.put.resource")
                                       ?.SetTag("resource", group.Key);

            await (group.Key switch
            {
                IResourceWithDto resourceWithDto =>
                     group.ToAsyncEnumerable()
                          .Where(async (model, cancellationToken) => await isResourceKeySupported(model.Key, cancellationToken))
                          .IterTaskParallel(async model => await putResource(resourceWithDto, model.Key.Name, model.ToDto(), model.Key.Parents, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken),
                _ => ValueTask.CompletedTask
            });
        }
    }
}