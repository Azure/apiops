using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace common;

public sealed record ResourceGraph
{
    private ResourceGraph(IEnumerable<IResource> topologicallySortedResources) =>
        TopologicallySortedResources = [.. topologicallySortedResources];

    public ImmutableArray<IResource> TopologicallySortedResources { get; }

    public static ResourceGraph From(IEnumerable<IResource> resources, CancellationToken cancellationToken)
    {
        var successorDictionary = new ConcurrentDictionary<IResource, ImmutableHashSet<IResource>>();
        var predecessorDictionary = new ConcurrentDictionary<IResource, ImmutableHashSet<IResource>>();

        resources.Iter(resource =>
        {
            var predecessors = resource.GetSortingPredecessors();

            predecessorDictionary.TryAdd(resource, predecessors);

            predecessors.Iter(predecessor =>
            {
                successorDictionary.AddOrUpdate(predecessor,
                                                [resource],
                                                (_, existing) => existing.Add(resource));
            }, cancellationToken);
        }, cancellationToken);

        var sortedResources = TopologicalSort(successorDictionary, predecessorDictionary, cancellationToken);

        return new(sortedResources);
    }

    /// <summary>
    /// Sorts resources such that each resource appears before its successors.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a circular dependency is detected.</exception>
    private static ImmutableArray<IResource> TopologicalSort(IDictionary<IResource, ImmutableHashSet<IResource>> successors,
                                                             IDictionary<IResource, ImmutableHashSet<IResource>> predecessors,
                                                             CancellationToken cancellationToken)
    {
        var resources = successors.Keys
                                  .Union(predecessors.Keys)
                                  .ToImmutableHashSet();

        var inDegrees = resources.ToDictionary(resource => resource,
                                               resource => predecessors.Find(resource)
                                                                       .IfNone(() => [])
                                                                       .Count);

        var rootResources = inDegrees.Choose(kvp => kvp.Value == 0 ? Option.Some(kvp.Key) : Option.None);

        var queue = new Queue<IResource>(rootResources);
        var sorted = new List<IResource>();

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resource = queue.Dequeue();
            sorted.Add(resource);

            successors.Find(resource)
                      .IfNone(() => [])
                      .Iter(successor =>
                      {
                          inDegrees[successor]--;
                          if (inDegrees[successor] == 0)
                          {
                              queue.Enqueue(successor);
                          }
                      }, cancellationToken);
        }

        if (sorted.Count != resources.Count)
        {
            var remaining = resources.Except(sorted).Select(resource => resource.GetType().Name);
            throw new InvalidOperationException($"The following resources have circular or invalid dependencies: {string.Join(", ", remaining)}.");
        }

        return [.. sorted];
    }

    public ImmutableHashSet<IResource> GetTraversalRootResources() =>
        [.. TopologicallySortedResources
                .Where(resource => resource.GetTraversalPredecessor().IsNone)];

    public ImmutableHashSet<IResource> GetTraversalSuccessors(IResource resource) =>
        [.. TopologicallySortedResources
                .Choose(potentialSuccessor => from predecessor in potentialSuccessor.GetTraversalPredecessor()
                                              where predecessor == resource
                                              select potentialSuccessor)];

    public ImmutableHashSet<IResource> GetSortingSuccessors(IResource resource) =>
        [.. from potentialSuccessor in TopologicallySortedResources
            where potentialSuccessor.GetSortingPredecessors().Contains(resource)
            select potentialSuccessor];
}

public static class ResourceGraphModule
{
    public static void ConfigureBuilder(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(GetResourceGraph);

    private static ResourceGraph GetResourceGraph(IServiceProvider provider)
    {
        var cancellationToken = CancellationToken.None;

        return ResourceGraph.From([ServicePolicyResource.Instance,
                                   ProductResource.Instance,
                                   ProductApiResource.Instance,
                                   ProductPolicyResource.Instance,
                                   ProductGroupResource.Instance,
                                   NamedValueResource.Instance,
                                   TagResource.Instance,
                                   TagApiResource.Instance,
                                   TagProductResource.Instance,
                                   ApiResource.Instance,
                                   ApiPolicyResource.Instance,
                                   ApiDiagnosticResource.Instance,
                                   BackendResource.Instance,
                                   GatewayResource.Instance,
                                   GatewayApiResource.Instance,
                                   LoggerResource.Instance,
                                   VersionSetResource.Instance,
                                   DiagnosticResource.Instance,
                                   GroupResource.Instance,
                                   SubscriptionResource.Instance,
                                   PolicyFragmentResource.Instance], cancellationToken);
    }
}