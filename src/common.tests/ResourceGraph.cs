using CsCheck;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace common.tests;

internal sealed class ResourceGraphTests
{
    public static CancellationToken CancellationToken => TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task TopologicalSortViaKahn_acyclic_graph_behaves_as_expected()
    {
        var gen = NodeGenerator.GenerateAcyclicGraph();

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (nodes, successors) = tuple;
            IEnumerable<int> getSuccessors(int node) =>
                successors.Find(node)
                          .IfNone(() => []);

            // Act
            var (sorted, remaining) = ResourceGraph.TopologicalSortViaKahn(nodes, getSuccessors, CancellationToken);

            // Assert that sorted contains exactly the distinct nodes
            await Assert.That(sorted).HasDistinctItems()
                        .And.IsEquivalentTo(nodes);

            // Assert that for each edge (from -> to), from appears before to in sorted
            var indexes = sorted.Select((node, index) => KeyValuePair.Create(node, index))
                                .ToImmutableDictionary();

            var pairIndexes = from kvp in successors
                              from successor in kvp.Value
                              select (FromIndex: indexes[kvp.Key], ToIndex: indexes[successor]);

            await Assert.That(pairIndexes)
                        .All(pair => pair.FromIndex < pair.ToIndex);

            // Assert that there are no remaining nodes
            await Assert.That(remaining).IsEmpty();
        });
    }

    [Test]
    public async Task TopologicalSortViaKahn_cyclic_graph_behaves_as_expected()
    {
        var gen = NodeGenerator.GenerateCyclicGraph();

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (nodes, successors) = tuple;
            IEnumerable<int> getSuccessors(int node) =>
                successors.Find(node)
                          .IfNone(() => []);

            // Act
            var (sorted, remaining) = ResourceGraph.TopologicalSortViaKahn(nodes, getSuccessors, CancellationToken);

            // Assert that there are remaining nodes
            await Assert.That(remaining).IsNotEmpty();

            // Assert that none of the remaining nodes are in sorted
            await Assert.That(remaining).DoesNotIntersectWith(sorted);

            // Assert that remaining + sorted = nodes
            await Assert.That(remaining.Concat(sorted)).IsEquivalentTo(nodes);

            // Assert that for each edge (from -> to) in sorted, from appears before to
            var indexes = sorted.Select((node, index) => KeyValuePair.Create(node, index))
                                .ToImmutableDictionary();

            var pairIndexes = successors.SelectMany(kvp => from value in kvp.Value
                                                           select (kvp.Key, value))
                                        .Choose(pair => from predecessorIndex in indexes.Find(pair.Key)
                                                        from successorIndex in indexes.Find(pair.value)
                                                        select (FromIndex: predecessorIndex, ToIndex: successorIndex));

            await Assert.That(pairIndexes)
                        .All(pair => pair.FromIndex < pair.ToIndex);
        });
    }

    [Test]
    public async Task TopologicalSortViaKahn_throws_when_a_successor_is_not_in_the_node_set()
    {
        var gen = from tuple in Gen.OneOf(NodeGenerator.GenerateCyclicGraph(), NodeGenerator.GenerateAcyclicGraph())
                  let nodes = tuple.Nodes
                  let successors = tuple.Successors
                  where successors.Count > 0
                  from pair in Gen.OneOfConst([.. successors])
                  from invalidSuccessor in Gen.Int.Where(invalid => nodes.Contains(invalid) is false)
                  let modifiedSuccessors = successors.SetItem(pair.Key,
                                                              pair.Value.Add(invalidSuccessor))
                  select (nodes, modifiedSuccessors);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (nodes, successors) = tuple;

            IEnumerable<int> getSuccessors(int node) =>
                successors.Find(node)
                          .IfNone(() => []);
            // Assert
            await Assert.That(() => ResourceGraph.TopologicalSortViaKahn(nodes, getSuccessors, CancellationToken))
                        .Throws<ArgumentException>();
        });
    }

    [Test]
    public async Task FindCycle_with_acyclic_graph_returns_None()
    {
        var gen = NodeGenerator.GenerateAcyclicGraph();

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (nodes, successors) = tuple;

            IEnumerable<int> getSuccessors(int node) =>
                successors.Find(node)
                          .IfNone(() => []);

            // Act
            var result = ResourceGraph.FindCycle(nodes, getSuccessors, CancellationToken);

            // Assert
            await Assert.That(result).IsNone();
        });
    }

    [Test]
    public async Task FindCycle_with_cyclic_graph_returns_cycle()
    {
        var gen = NodeGenerator.GenerateCyclicGraph();

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (nodes, successors) = tuple;

            IEnumerable<int> getSuccessors(int node) =>
                successors.Find(node)
                          .IfNone(() => []);

            // Act
            var result = ResourceGraph.FindCycle(nodes, getSuccessors, CancellationToken);

            // Assert that a cycle is found with at least 2 nodes
            var cycles = await Assert.That(result).IsSome();
            await Assert.That(cycles).Count().IsGreaterThanOrEqualTo(2);

            // Assert that the first and last nodes in the cycle are the same
            var (first, last) = (cycles.First(), cycles.Last());
            await Assert.That(first).IsEqualTo(last);

            // Assert that each consecutive pair in the cycle represents a valid edge
            var cyclePairs = cycles.Zip(cycles.Skip(1), (from, to) => (From: from, To: to));
            await Assert.That(cyclePairs)
                        .All(pair => getSuccessors(pair.From).Contains(pair.To));
        });
    }
}

file static class NodeGenerator
{
    public static Gen<(ImmutableHashSet<int> Nodes, ImmutableDictionary<int, ImmutableHashSet<int>> Successors)> GenerateAcyclicGraph() =>
        from nodes in Gen.Int.HashSetOf()
        from ranks in Generator.Traverse(nodes, node => from rank in Gen.Int
                                                        select KeyValuePair.Create(node, rank))
                               .Select(ranks => ranks.ToImmutableDictionary())
        from successors in Generator.Traverse(nodes,
                                              node => from successors in Generator.SubSetOf(nodes)
                                                      let filtered = from successor in successors
                                                                     where ranks[successor] > ranks[node]
                                                                     select successor
                                                      select KeyValuePair.Create(node, filtered.ToImmutableHashSet()))
                                    .Select(successors => successors.ToImmutableDictionary())
        select (nodes, successors);

    public static Gen<(ImmutableHashSet<int> Nodes, ImmutableDictionary<int, ImmutableHashSet<int>> Successors)> GenerateCyclicGraph() =>
        from nodes in Gen.Int.HashSetOf()
        from successors in Generator.Traverse(nodes,
                                              node => from successors in Generator.SubSetOf(nodes)
                                                      let filtered = from successor in successors
                                                                     where successor != node
                                                                     select successor
                                                      select KeyValuePair.Create(node, filtered.ToImmutableHashSet()))
                                    .Select(successors => successors.ToImmutableDictionary())
            // Ensure we have a cycle by adding a resource to its grandchild's successors
        let grandChildren = successors.SelectMany(kvp => from successor in kvp.Value
                                                         from grandChild in successors.Find(successor).IfNone(() => [])
                                                         select (GrandParent: kvp.Key, GrandChild: grandChild))
                                     .ToImmutableArray()
        where grandChildren.Length > 0
        from cyclePair in Gen.OneOfConst([.. grandChildren])
        let grandParent = cyclePair.GrandParent
        let grandChild = cyclePair.GrandChild
        let successorsWithCycle = successors.TryGetValue(grandChild, out var existingSuccessors)
                                      ? successors.SetItem(grandChild,
                                                           existingSuccessors.Add(grandParent))
                                      : successors.Add(grandChild, [grandParent])
        select (nodes, successorsWithCycle);
}
