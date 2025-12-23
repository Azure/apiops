using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using System;
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
    public async Task Resources_are_only_put_once()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  select fixture with
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
                      }
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var putResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(putResources).HasDistinctItems();
        });
    }

    [Test]
    public async Task Put_resources_must_be_in_the_file_system()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  from isInFileSystem in GenerateResourceKeyPredicate()
                  select (isInFileSystem, fixture with
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
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (isInFileSystem, fixture) = tuple;
            var putResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(putResources).All(isInFileSystem);
        });
    }

    [Test]
    public async Task Put_resources_must_come_from_resources_to_process()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  select (resourcesToProcess, fixture with
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
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourcesToProcess, fixture) = tuple;
            var putResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(putResources).All(resourcesToProcess.Contains);
        });
    }

    [Test]
    public async Task Eligible_resources_are_put()
    {
        var gen = from eligibleResource in Generator.ResourceKey
                  from fixture in Fixture.Generate()
                  select (eligibleResource, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return ImmutableHashSet.Create(eligibleResource);
                      },
                      IsResourceInFileSystem = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return key == eligibleResource;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (eligibleResource, fixture) = tuple;
            var putResources = ImmutableHashSet<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(putResources).Contains(eligibleResource);
        });
    }

    [Test]
    public async Task Resources_are_only_deleted_once()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  select fixture with
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
                      }
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var deletedResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(deletedResources).HasDistinctItems();
        });
    }

    [Test]
    public async Task Deleted_resources_cannot_be_in_file_system()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  from isNotInFileSystem in GenerateResourceKeyPredicate()
                  select (isNotInFileSystem, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return resourcesToProcess;
                      },
                      IsResourceInFileSystem = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return isNotInFileSystem(key) is false;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (isNotInFileSystem, fixture) = tuple;
            var deletedResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(deletedResources).All(isNotInFileSystem);
        });
    }

    [Test]
    public async Task Deleted_resources_must_come_from_resources_to_process()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourcesToProcess in Generator.ResourceKey.HashSetOf()
                  select (resourcesToProcess, fixture with
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
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourcesToProcess, fixture) = tuple;
            var deletedResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(deletedResources).All(resourcesToProcess.Contains);
        });
    }

    [Test]
    public async Task Eligible_resources_are_deleted()
    {
        var gen = from eligibleResource in Generator.ResourceKey
                  from fixture in Fixture.Generate()
                  select (eligibleResource, fixture with
                  {
                      ListResourcesToProcess = async _ =>
                      {
                          await ValueTask.CompletedTask;
                          return [eligibleResource];
                      },
                      IsResourceInFileSystem = async (key, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return key != eligibleResource;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (eligibleResource, fixture) = tuple;
            var deletedResources = ImmutableHashSet<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            await Assert.That(deletedResources).Contains(eligibleResource);
        });
    }

    [Test]
    public async Task Predecessors_are_put_before_successors()
    {
        var gen = from relationships in Fixture.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  select fixture with
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
                      }
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var putResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            var relationships = await fixture.GetCurrentRelationships(CancellationToken);

            var putDictionary = putResources.Select((key, index) => (key, index))
                                            .ToImmutableDictionary(pair => pair.key, pair => pair.index);

            var pairsFromSuccessors = putResources.SelectMany(key => from successor in relationships.Successors.Find(key).IfNone(() => [])
                                                                     select (Predecessor: key, Successor: successor));
            var pairsFromPredecessors = putResources.SelectMany(key => from predecessor in relationships.Predecessors.Find(key).IfNone(() => [])
                                                                       select (Predecessor: predecessor, Successor: key));

            var indices = pairsFromPredecessors.Concat(pairsFromSuccessors)
                                               .Select(pair => (PredecessorIndex: putDictionary[pair.Predecessor],
                                                                SuccessorIndex: putDictionary[pair.Successor]));

            await Assert.That(indices)
                        .All(index => index.PredecessorIndex < index.SuccessorIndex);
        });
    }

    [Test]
    public async Task Successors_are_deleted_before_predecessors()
    {
        var gen = from relationships in Fixture.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  select fixture with
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
                      }
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var deletedResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = async (key, _) =>
                {
                    await ValueTask.CompletedTask;
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                }
            };

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(CancellationToken);

            // Assert
            var relationships = await fixture.GetPreviousRelationships(CancellationToken);

            var deletedDictionary = deletedResources.Select((key, index) => (key, index))
                                                    .ToImmutableDictionary(pair => pair.key, pair => pair.index);

            var pairsFromSuccessors = deletedResources.SelectMany(key => from successor in relationships.Successors.Find(key).IfNone(() => [])
                                                                         select (Predecessor: key, Successor: successor));
            var pairsFromPredecessors = deletedResources.SelectMany(key => from predecessor in relationships.Predecessors.Find(key).IfNone(() => [])
                                                                           select (Predecessor: predecessor, Successor: key));

            var indices = pairsFromPredecessors.Concat(pairsFromSuccessors)
                                               .Select(pair => (PredecessorIndex: deletedDictionary[pair.Predecessor],
                                                                SuccessorIndex: deletedDictionary[pair.Successor]));

            await Assert.That(indices)
                        .All(index => index.PredecessorIndex > index.SuccessorIndex);
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
            from currentRelationships in GenerateRelationships()
            from previousRelationships in GenerateRelationships()
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

        public static Gen<Relationships> GenerateRelationships() =>
            from dtos in Generator.ResourceDtos
            select Relationships.From(dtos.Keys.Aggregate(new List<(ResourceKey, Option<ResourceKey>)>(),
                                                          (list, key) =>
                                                          {
                                                              // Always register the resource itself to pass Relationships.Validate.
                                                              list.Add((key, Option<ResourceKey>.None()));

                                                              // Register the parent -> child edge when applicable.
                                                              switch (key.Parents.ToImmutableArray())
                                                              {
                                                                  case []:
                                                                      break;
                                                                  case [.. var parentParents, var parent]:
                                                                      list.Add((new ResourceKey
                                                                      {
                                                                          Name = parent.Name,
                                                                          Resource = parent.Resource,
                                                                          Parents = ParentChain.From(parentParents)
                                                                      }, Option.Some(key)));
                                                                      break;
                                                              }

                                                              return list;
                                                          }), CancellationToken.None);
    }

    public static Gen<Func<ResourceKey, bool>> GenerateResourceKeyPredicate() =>
        from x in Gen.Int[2, 10]
        select (Func<ResourceKey, bool>)(key => key.GetHashCode() % x == 0);
}