using common;
using common.tests;
using CsCheck;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal sealed class RelationshipsTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task From_normalizes_graph_for_all_generated_pairs()
    {
        var gen = from nodes in Generator.ResourceKey.HashSetOf()
                  let nodeList = nodes.ToList()
                  from first in Gen.Shuffle(nodeList)
                  from second in Gen.Shuffle(nodeList)
                  select first.Zip(second);

        await gen.SampleAsync(async pairs =>
        {
            // Act
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that all nodes appear in both predecessors and successors
            var nodes = pairs.SelectMany(pair => ImmutableArray.Create(pair.First, pair.Second))
                             .ToImmutableHashSet();

            await Assert.That(nodes)
                        .All(node => relationships.Predecessors.ContainsKey(node) && relationships.Successors.ContainsKey(node));
        });
    }

    [Test]
    public async Task Validate_fails_when_a_predecessor_is_missing()
    {
        var gen = from nodes in Generator.ResourceKey.HashSetOf()
                  let ranks = nodes.ToImmutableDictionary(node => node, node => node.GetHashCode())
                  from pairs in Generator.Traverse(nodes,
                                                   node => from successors in Generator.SubSetOf(nodes)
                                                           select from successor in successors
                                                                  where ranks[successor] > ranks[node]
                                                                  select (node, successor))
                                         .Select(pairs => pairs.SelectMany(x => x))
                  from missingPredecessor in Generator.ResourceKey
                  where nodes.Contains(missingPredecessor) is false
                  from successorWithMissingPredecessor in Generator.ResourceKey
                  where nodes.Contains(successorWithMissingPredecessor) is false
                  let resourceInSnapshot = (Func<ResourceKey, bool>)(resource => resource == missingPredecessor ? false : true)
                  let finalPairs = pairs.Append((missingPredecessor, successorWithMissingPredecessor)).ToImmutableArray()
                  select (finalPairs, resourceInSnapshot, missingPredecessor, successorWithMissingPredecessor);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (pairs, resourceInSnapshot, missingPredecessor, successorWithMissingPredecessor) = tuple;
            var relationships = Relationships.From(pairs, CancellationToken);

            // Act
            var errors = relationships.Validate(resourceInSnapshot, CancellationToken);

            // Assert that the missing predecessor error is reported
            await Assert.That(errors)
                        .Contains(error => error is ValidationError.MissingPredecessor missing
                                           && missing.Predecessor == missingPredecessor
                                           && missing.Successor == successorWithMissingPredecessor);
        });
    }

    [Test]
    public async Task Validate_fails_when_a_cycle_exists()
    {
        var gen = from nodes in Generator.ResourceKey.HashSetOf()
                  from pairs in Generator.Traverse(nodes,
                                                   node => from successors in Generator.SubSetOf(nodes)
                                                           select from successor in successors
                                                                  where successor != node
                                                                  select (node, successor))
                                         .Select(pairs => pairs.SelectMany(x => x))
                      // Ensure we have a cycle by adding a resource to its grandchild's successors
                  let successors = pairs.GroupBy(pair => pair.node, pair => pair.successor)
                                        .ToImmutableDictionary(@group => @group.Key, @group => @group.ToImmutableHashSet())
                  let grandChildren = successors.SelectMany(kvp => from successor in kvp.Value
                                                                   from grandChild in successors.Find(successor).IfNone(() => [])
                                                                   select (GrandParent: kvp.Key, GrandChild: grandChild))
                                                .ToImmutableArray()
                  where grandChildren.Length > 0
                  from cyclePair in Gen.OneOfConst([.. grandChildren])
                  let grandParent = cyclePair.GrandParent
                  let grandChild = cyclePair.GrandChild
                  let finalPairs = pairs.Append((grandChild, grandParent)).ToImmutableArray()
                  select finalPairs;

        await gen.SampleAsync(async pairs =>
        {
            // Arrange
            var relationships = Relationships.From(pairs, CancellationToken);

            // Act
            var errors = relationships.Validate(resource => true, CancellationToken);

            // Assert that a cycle error is reported
            await Assert.That(errors)
                        .Contains(error => error is ValidationError.Cycle cycle);
        });
    }
}
