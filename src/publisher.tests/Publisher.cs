using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.tests;

internal sealed class RunPublisherTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Put_resources_at_most_once()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  let putResources = new ConcurrentQueue<ResourceKey>()
                  select (putResources, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return resourcesToProcess;
                      },
                      IsResourceInFileSystem = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      },
                      PutResource = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (putResources, fixture) = tuple;
            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert that no resource was put more than once
            await Assert.That(putResources)
                        .HasDistinctItems();
        });
    }

    [Test]
    public async Task Delete_resources_at_most_once()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  let deletedResources = new ConcurrentQueue<ResourceKey>()
                  select (deletedResources, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return resourcesToProcess;
                      },
                      IsResourceInFileSystem = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return false;
                      },
                      DeleteResource = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (deletedResources, fixture) = tuple;
            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert that no resource was deleted more than once
            await Assert.That(deletedResources)
                        .HasDistinctItems();
        });
    }

    [Test]
    public async Task Only_eligible_resources_are_put()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  from isInFileSystem in GenerateResourceKeyPredicate()
                  let isEligible = (Func<ResourceKey, bool>)(key => isInFileSystem(key) && resourcesToProcess.Contains(key))
                  let putResources = new ConcurrentQueue<ResourceKey>()
                  select (isEligible, putResources, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return resourcesToProcess;
                      },
                      IsResourceInFileSystem = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return isInFileSystem(key);
                      },
                      PutResource = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (isEligible, putResources, fixture) = tuple;
            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert that all put resources are eligible
            await Assert.That(putResources).All(isEligible);
        });
    }

    [Test]
    public async Task Only_eligible_resources_are_deleted()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  from isInFileSystem in GenerateResourceKeyPredicate()
                  let isEligible = (Func<ResourceKey, bool>)(key => isInFileSystem(key) is false && resourcesToProcess.Contains(key))
                  let deletedResources = new ConcurrentQueue<ResourceKey>()
                  select (isEligible, deletedResources, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return resourcesToProcess;
                      },
                      IsResourceInFileSystem = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return isInFileSystem(key);
                      },
                      DeleteResource = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (isEligible, deletedResources, fixture) = tuple;
            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert that all deleted resources are eligible
            await Assert.That(deletedResources).All(isEligible);
        });
    }

    [Test]
    public async Task Predecessors_are_put_before_successors()
    {
        var gen = from relationships in Common.GenerateRelationships()
                  let putResources = new ConcurrentQueue<ResourceKey>()
                  from fixture in Fixture.Generate()
                  select (relationships, putResources, fixture with
                  {
                      GetCurrentRelationships = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return relationships;
                      },
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return [.. relationships.Predecessors.Keys, .. relationships.Successors.Keys];
                      },
                      IsResourceInFileSystem = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return true;
                      },
                      PutResource = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          putResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (relationships, putResources, fixture) = tuple;
            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert that all predecessors are put before their successors
            var putResourceIndex = putResources.Select((key, index) => (key, index))
                                               .ToImmutableDictionary(pair => pair.key, pair => pair.index);

            var putPairIndexes = from kvp in relationships.Successors
                                 let predecessor = kvp.Key
                                 from successor in kvp.Value
                                 where putResourceIndex.ContainsKey(predecessor) && putResourceIndex.ContainsKey(successor)
                                 let predecessorIndex = putResourceIndex[predecessor]
                                 let successorIndex = putResourceIndex[successor]
                                 select (predecessorIndex, successorIndex);

            await Assert.That(putPairIndexes)
                        .All(index => index.predecessorIndex < index.successorIndex);
        });
    }

    [Test]
    public async Task Successors_are_deleted_before_predecessors()
    {
        var gen = from relationships in Common.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  let deletedResources = new ConcurrentQueue<ResourceKey>()
                  select (relationships, deletedResources, fixture with
                  {
                      GetPreviousRelationships = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return relationships;
                      },
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return [.. relationships.Predecessors.Keys, .. relationships.Successors.Keys];
                      },
                      IsResourceInFileSystem = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return false;
                      },
                      DeleteResource = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          deletedResources.Enqueue(key);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (relationships, deletedResources, fixture) = tuple;
            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert that all successors are deleted before their predecessors
            var deletedResourceIndex = deletedResources.Select((key, index) => (key, index))
                                                       .ToImmutableDictionary(pair => pair.key, pair => pair.index);
            var deletedPairIndexes = from kvp in relationships.Predecessors
                                     let successor = kvp.Key
                                     from predecessor in kvp.Value
                                     where deletedResourceIndex.ContainsKey(predecessor) && deletedResourceIndex.ContainsKey(successor)
                                     let predecessorIndex = deletedResourceIndex[predecessor]
                                     let successorIndex = deletedResourceIndex[successor]
                                     select (predecessorIndex, successorIndex);

            await Assert.That(deletedPairIndexes)
                        .All(index => index.predecessorIndex > index.successorIndex);
        });
    }

    private sealed record Fixture
    {
        public required GetCurrentRelationships GetCurrentRelationships { get; init; }
        public required GetPreviousRelationships GetPreviousRelationships { get; init; }
        public required ListResourcesToProcess ListResourcesToProcess { get; init; }
        public required IsResourceInFileSystem IsResourceInFileSystem { get; init; }
        public required PutResource PutResource { get; init; }
        public required DeleteResource DeleteResource { get; init; }

        public RunPublisher Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(GetCurrentRelationships)
                    .AddSingleton(GetPreviousRelationships)
                    .AddSingleton(ListResourcesToProcess)
                    .AddSingleton(IsResourceInFileSystem)
                    .AddSingleton(PutResource)
                    .AddSingleton(DeleteResource)
                    .AddTestActivitySource()
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return PublisherModule.ResolveRunPublisher(provider);
        }

        public static Gen<Fixture> Generate() =>
            from currentRelationships in Common.GenerateRelationships()
            from previousRelationships in Common.GenerateRelationships()
            from resourcesToProcess in Generator.ResourceKey.HashSetOf()
            from resourceKeyPredicate in GenerateResourceKeyPredicate()
            select new Fixture
            {
                GetCurrentRelationships = async _ =>
                {
                    await ValueTask.CompletedTask;
                    return currentRelationships;
                },
                GetPreviousRelationships = async _ =>
                {
                    await ValueTask.CompletedTask;
                    return previousRelationships;
                },
                ListResourcesToProcess = async _ =>
                {
                    await ValueTask.CompletedTask;
                    return resourcesToProcess;
                },
                IsResourceInFileSystem = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    return resourceKeyPredicate(key);
                },
                PutResource = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                },
                DeleteResource = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                }
            };
    }

    public static Gen<Func<ResourceKey, bool>> GenerateResourceKeyPredicate() =>
        from x in Gen.Int[2, 10]
        select (Func<ResourceKey, bool>)(key => key.GetHashCode() % x == 0);
}