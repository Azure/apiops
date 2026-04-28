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
    // We list traversal successors a lot, caching to optimize
    private readonly Lazy<ImmutableDictionary<IResource, ImmutableHashSet<IResource>>> traversalSuccessors;

    private ResourceGraph(IEnumerable<IResource> topologicallySortedResources)
    {
        TopologicallySortedResources = [.. topologicallySortedResources];
        traversalSuccessors = new(() =>
            TopologicallySortedResources.ToImmutableDictionary(
                resource => resource,
                resource => TopologicallySortedResources
                            .Choose(potentialSuccessor => from predecessor in potentialSuccessor.GetTraversalPredecessor()
                                                          where predecessor == resource
                                                          select potentialSuccessor)
                            .ToImmutableHashSet()));
    }

    public ImmutableArray<IResource> TopologicallySortedResources { get; }

    public static ResourceGraph From(IEnumerable<IResource> resources, CancellationToken cancellationToken)
    {
        var successorDictionary = new ConcurrentDictionary<IResource, ImmutableHashSet<IResource>>();
        var predecessorDictionary = new ConcurrentDictionary<IResource, ImmutableHashSet<IResource>>();

        resources.Iter(resource =>
        {
            var predecessors = resource.ListDependencies();

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

        IEnumerable<IResource> getSuccessors(IResource resource) =>
            successors.Find(resource)
                      .IfNone(() => []);

        var (sorted, remaining) = TopologicalSortViaKahn(resources, getSuccessors, cancellationToken);
        if (remaining.Count > 0)
        {
            var toString = (IEnumerable<IResource> resources) => resources.Select(resource => resource.GetType().Name);

            FindCycle(remaining, getSuccessors, cancellationToken)
                .Match(cycle => throw new InvalidOperationException($"Found cycle: {string.Join(" -> ", toString(cycle))}."),
                       () => throw new InvalidOperationException($"The following resources have circular or invalid dependencies: {string.Join(", ", toString(remaining))}."));
        }

        return [.. sorted];
    }

    public ImmutableHashSet<IResource> ListTraversalRootResources() =>
        [.. TopologicallySortedResources
                .Where(resource => resource.GetTraversalPredecessor().IsNone)];

    public ImmutableHashSet<IResource> ListTraversalSuccessors(IResource resource) =>
        traversalSuccessors.Value
                           .Find(resource)
                           .IfNone(() => []);

    public ImmutableHashSet<IResource> ListDependents(IResource resource) =>
        [.. from potentialDependent in TopologicallySortedResources
            where potentialDependent.ListDependencies().Contains(resource)
            select potentialDependent];

    /// <summary>
    /// Performs a topological sort using Kahn's algorithm.
    /// </summary>
    /// <param name="nodes">The nodes to sort.</param>
    /// <param name="getSuccessors">Returns the outgoing successors for a node. They must be a subset of the <paramref name="nodes"/> parameter.</param>
    /// <param name="cancellationToken">Cancellation token used to stop processing.</param>
    /// <returns>
    /// A pair where <c>Sorted</c> contains nodes ordered such that each node appears before its successors,
    /// and <c>Remaining</c> contains any nodes that could not be sorted due to cycles.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="getSuccessors"/> yields a successor that is not present in <paramref name="nodes"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public static (ImmutableArray<T> Sorted, ImmutableHashSet<T> Remaining) TopologicalSortViaKahn<T>(
        IEnumerable<T> nodes,
        Func<T, IEnumerable<T>> getSuccessors,
        CancellationToken cancellationToken = default) where T : notnull
    {
        var declaredNodes = new HashSet<T>();
        var seenSuccessors = new HashSet<T>();
        var incomingEdges = new Dictionary<T, int>();
        var nodeSuccessors = new Dictionary<T, ImmutableArray<T>>();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (declaredNodes.Add(node) is false)
            {
                continue;
            }

            incomingEdges.TryAdd(node, 0);

            var successors = getSuccessors(node).ToImmutableArray();
            nodeSuccessors[node] = successors;

            foreach (var successor in successors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                seenSuccessors.Add(successor);

                if (incomingEdges.TryGetValue(successor, out var count))
                {
                    incomingEdges[successor] = count + 1;
                }
                else
                {
                    incomingEdges[successor] = 1;
                }
            }
        }

        var unknownSuccessors = seenSuccessors.Except(declaredNodes).ToImmutableArray();
        if (unknownSuccessors.Length > 0)
        {
            throw new ArgumentException(
                $"The graph contains successors not present in {nameof(nodes)}: {string.Join(", ", unknownSuccessors)}.",
                nameof(nodes));
        }

        var inDegrees = declaredNodes.ToDictionary(node => node,
                                                   node => incomingEdges.Find(node)
                                                                        .IfNone(() => 0));

        var queue = new Queue<T>([.. from kvp in inDegrees
                                     where kvp.Value == 0
                                     select kvp.Key]);

        var sorted = new List<T>();

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = queue.Dequeue();
            sorted.Add(node);

            var successors = nodeSuccessors.Find(node).IfNone(() => []);
            foreach (var successor in successors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (inDegrees.ContainsKey(successor) is false)
                {
                    continue;
                }

                var updatedDegree = --inDegrees[successor];
                if (updatedDegree == 0)
                {
                    queue.Enqueue(successor);
                }
            }
        }

        var remaining = from kvp in inDegrees
                        where kvp.Value > 0
                        select kvp.Key;

        return ([.. sorted], [.. remaining]);
    }

    public static Option<ImmutableArray<T>> FindCycle<T>(IEnumerable<T> nodes, Func<T, IEnumerable<T>> getSuccessors, CancellationToken cancellationToken) where T : notnull
    {
        var cycle = Option<ImmutableArray<T>>.None();
        var parents = new Dictionary<T, T>();
        var visiting = new HashSet<T>();
        var visited = new HashSet<T>();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cycle.IsSome)
            {
                break;
            }

            if (visiting.Contains(node) is false
                && visited.Contains(node) is false)
            {
                visit(node);
            }
        }

        return cycle;

        void visit(T node)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cycle.IsSome)
            {
                return;
            }

            visiting.Add(node);

            foreach (var successor in getSuccessors(node))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (cycle.IsSome)
                {
                    return;
                }

                if (visiting.Contains(successor))
                {
                    cycle = buildCycle(node, successor);
                    return;
                }

                if (visited.Contains(successor) is false)
                {
                    parents[successor] = node;
                    visit(successor);
                }
            }

            visiting.Remove(node);
            visited.Add(node);
        }

        ImmutableArray<T> buildCycle(T from, T to)
        {
            var comparer = EqualityComparer<T>.Default;
            if (comparer.Equals(from, to))
            {
                return [from, to];
            }

            var cycleNodes = new List<T> { to, from };
            var current = from;

            while (comparer.Equals(current, to) is false)
            {
                if (parents.TryGetValue(current, out var parent) is false)
                {
                    cycleNodes.Add(to);
                    break;
                }

                current = parent;
                cycleNodes.Add(current);
            }

            cycleNodes.Reverse();
            return [.. cycleNodes];
        }
    }

    /// <summary>
    /// Gets strongly connected components using Tarjan's algorithm.
    /// </summary>
    public static IEnumerable<ImmutableHashSet<T>> GetStronglyConnectedComponents<T>(IEnumerable<T> nodes, Func<T, IEnumerable<T>> getEdges, CancellationToken cancellationToken) where T : notnull
    {
        var index = 0;
        var nodeIndexes = new Dictionary<T, int>();
        var nodeLowLinks = new Dictionary<T, int>();
        var stack = new Stack<T>();
        var onStackNodes = new HashSet<T>();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (nodeIndexes.ContainsKey(node) is false)
            {
                foreach (var component in strongConnect(node))
                {
                    yield return component;
                }
            }
        }

        IEnumerable<ImmutableHashSet<T>> strongConnect(T node)
        {
            cancellationToken.ThrowIfCancellationRequested();

            nodeIndexes[node] = index;
            nodeLowLinks[node] = index;
            index++;
            stack.Push(node);
            onStackNodes.Add(node);

            foreach (var edge in getEdges(node))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (nodeIndexes.ContainsKey(edge) is false)
                {
                    foreach (var component in strongConnect(edge))
                    {
                        yield return component;
                    }

                    nodeLowLinks[node] = Math.Min(nodeLowLinks[node], nodeLowLinks[edge]);
                }
                else if (onStackNodes.Contains(edge))
                {
                    nodeLowLinks[node] = Math.Min(nodeLowLinks[node], nodeIndexes[edge]);
                }
            }

            if (nodeLowLinks[node] == nodeIndexes[node])
            {
                var stronglyConnectedComponents = new List<T>();

                while (stack.TryPop(out var other))
                {
                    onStackNodes.Remove(other);
                    stronglyConnectedComponents.Add(other);

                    if (node.Equals(other))
                    {
                        break;
                    }
                }

                yield return [.. stronglyConnectedComponents];
            }
        }
    }
}

public static class ResourceGraphModule
{
    public static void ConfigureResourceGraph(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveResourceGraph);

    private static ResourceGraph ResolveResourceGraph(IServiceProvider provider)
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
                       ApiReleaseResource.Instance,
                       ApiPolicyResource.Instance,
                       ApiDiagnosticResource.Instance,
                       ApiOperationResource.Instance,
                       ApiOperationPolicyResource.Instance,
                       BackendResource.Instance,
                       GatewayResource.Instance,
                       GatewayApiResource.Instance,
                       LoggerResource.Instance,
                       VersionSetResource.Instance,
                       DiagnosticResource.Instance,
                       GroupResource.Instance,
                       SubscriptionResource.Instance,
                       PolicyFragmentResource.Instance,
                       WorkspaceResource.Instance,
                       WorkspaceGroupResource.Instance,
                       WorkspaceNamedValueResource.Instance,
                       WorkspaceBackendResource.Instance,
                       WorkspaceLoggerResource.Instance,
                       WorkspaceTagResource.Instance,
                       WorkspaceVersionSetResource.Instance,
                       WorkspaceApiResource.Instance,
                       WorkspaceApiReleaseResource.Instance,
                       WorkspaceApiPolicyResource.Instance,
                       WorkspaceApiDiagnosticResource.Instance,
                       WorkspaceApiOperationResource.Instance,
                       WorkspaceApiOperationPolicyResource.Instance,
                       WorkspaceTagApiResource.Instance,
                       WorkspacePolicyFragmentResource.Instance,
                       WorkspaceProductResource.Instance,
                       WorkspaceTagProductResource.Instance,
                       WorkspaceProductApiResource.Instance,
                       WorkspaceProductPolicyResource.Instance,
                       WorkspaceProductGroupResource.Instance,
                       WorkspaceSubscriptionResource.Instance,
                       WorkspaceDiagnosticResource.Instance], cancellationToken);
    }
}