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
    [Test]
    public async Task Resources_present_in_file_system_are_put_exactly_once()
    {
        var gen = from resourcesInFileSystem in Generator.ResourceKey.HashSetOf()
                  from fixture in Fixture.Generate()
                  select (resourcesInFileSystem, fixture with
                  {
                      ListResourcesToProcess = _ => ValueTask.FromResult(resourcesInFileSystem),
                      IsResourceInFileSystem = (key, _) => ValueTask.FromResult(resourcesInFileSystem.Contains(key))
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourcesInFileSystem, fixture) = tuple;
            var putResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = (key, _) =>
                {
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                    return ValueTask.CompletedTask;
                }
            };

            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(cancellationToken);

            // Assert
            await Assert.That(resourcesInFileSystem.SetEquals(putResources)).IsTrue();
            await Assert.That(putResources).HasDistinctItems();
        });
    }

    [Test]
    public async Task Resources_missing_from_file_system_are_deleted()
    {
        var gen = from resourcesMissingFromFileSystem in Generator.ResourceKey.HashSetOf()
                  from fixture in Fixture.Generate()
                  select (resourcesMissingFromFileSystem, fixture with
                  {
                      ListResourcesToProcess = _ => ValueTask.FromResult(resourcesMissingFromFileSystem),
                      IsResourceInFileSystem = (key, _) => ValueTask.FromResult(resourcesMissingFromFileSystem.Contains(key) is false)
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (resourcesMissingFromFileSystem, fixture) = tuple;
            var deletedResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = (key, _) =>
                {
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                    return ValueTask.CompletedTask;
                }
            };

            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(cancellationToken);

            // Assert
            await Assert.That(resourcesMissingFromFileSystem.SetEquals(deletedResources)).IsTrue();
            await Assert.That(deletedResources).HasDistinctItems();
        });
    }

    [Test]
    public async Task Predecessors_are_put_before_successors()
    {
        var gen = from relationships in GenerateRelationships()
                  from fixture in Fixture.Generate()
                  select (relationships, fixture with
                  {
                      GetRelationships = (_, _) => ValueTask.FromResult(relationships),
                      ListResourcesToProcess = _ => ValueTask.FromResult(ImmutableHashSet.CreateRange([.. relationships.Predecessors.Keys, .. relationships.Successors.Keys])),
                      IsResourceInFileSystem = (_, _) => ValueTask.FromResult(true)
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (relationships, fixture) = tuple;

            var putResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                PutResource = (key, _) =>
                {
                    ImmutableInterlocked.Update(ref putResources, resources => resources.Add(key));
                    return ValueTask.CompletedTask;
                }
            };

            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(cancellationToken);

            // Assert
            var predecessorSuccessors = from kvp in relationships.Successors
                                        from successor in kvp.Value
                                        select (Predecessor: kvp.Key, Successor: successor);

            await Assert.That(predecessorSuccessors)
                        .All()
                        .Satisfy(kvp => putResources.IndexOf(kvp.Predecessor) < putResources.IndexOf(kvp.Successor),
                                 predecessorHasLowerIndex => predecessorHasLowerIndex.IsTrue());
        });
    }

    private static Gen<Relationships> GenerateRelationships() =>
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

    [Test]
    public async Task Successors_are_deleted_before_predecessors()
    {
        var gen = from relationships in GenerateRelationships()
                  from fixture in Fixture.Generate()
                  select (relationships, fixture with
                  {
                      GetRelationships = (_, _) => ValueTask.FromResult(relationships),
                      CommitIdWasPassed = () => true,
                      GetCurrentCommitFileOperations = () => Option.Some(new FileOperations
                      {
                          ReadFile = (_, _) => ValueTask.FromResult(Option<BinaryData>.None()),
                          GetSubDirectories = _ => Option.None,
                          EnumerateServiceDirectoryFiles = () => []
                      }),
                      GetPreviousCommitFileOperations = () => Option.Some(new FileOperations
                      {
                          ReadFile = (_, _) => ValueTask.FromResult(Option<BinaryData>.None()),
                          GetSubDirectories = _ => Option.None,
                          EnumerateServiceDirectoryFiles = () => []
                      }),
                      ListResourcesToProcess = _ => ValueTask.FromResult(ImmutableHashSet.CreateRange([.. relationships.Predecessors.Keys, .. relationships.Successors.Keys])),
                      IsResourceInFileSystem = (_, _) => ValueTask.FromResult(false)
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (relationships, fixture) = tuple;

            var deletedResources = ImmutableArray<ResourceKey>.Empty;

            fixture = fixture with
            {
                DeleteResource = (key, _) =>
                {
                    ImmutableInterlocked.Update(ref deletedResources, resources => resources.Add(key));
                    return ValueTask.CompletedTask;
                }
            };

            var cancellationToken = TestContext.Current!.Execution.CancellationToken;

            var runPublisher = fixture.Resolve();

            // Act
            await runPublisher(cancellationToken);

            // Assert
            var predecessorSuccessors = from kvp in relationships.Successors
                                        from successor in kvp.Value
                                        select (Predecessor: kvp.Key, Successor: successor);

            await Assert.That(predecessorSuccessors)
                        .All()
                        .Satisfy(kvp => deletedResources.IndexOf(kvp.Predecessor) > deletedResources.IndexOf(kvp.Successor),
                                 successorHasLowerIndex => successorHasLowerIndex.IsTrue());
        });
    }

    private sealed record Fixture
    {
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required GetCurrentCommitFileOperations GetCurrentCommitFileOperations { get; init; }
        public required GetPreviousCommitFileOperations GetPreviousCommitFileOperations { get; init; }
        public required GetLocalFileOperations GetLocalFileOperations { get; init; }
        public required GetRelationships GetRelationships { get; init; }
        public required ListResourcesToProcess ListResourcesToProcess { get; init; }
        public required IsResourceInFileSystem IsResourceInFileSystem { get; init; }
        public required PutResource PutResource { get; init; }
        public required DeleteResource DeleteResource { get; init; }

        public RunPublisher Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(CommitIdWasPassed)
                    .AddSingleton(GetCurrentCommitFileOperations)
                    .AddSingleton(GetPreviousCommitFileOperations)
                    .AddSingleton(GetLocalFileOperations)
                    .AddSingleton(GetRelationships)
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
            Gen.Const(new Fixture
            {
                CommitIdWasPassed = () => false,
                GetCurrentCommitFileOperations = () => Option.None,
                GetPreviousCommitFileOperations = () => Option.None,
                GetLocalFileOperations = () => new FileOperations
                {
                    ReadFile = (_, _) => ValueTask.FromResult(Option<BinaryData>.None()),
                    GetSubDirectories = _ => Option.None,
                    EnumerateServiceDirectoryFiles = () => []
                },
                GetRelationships = (_, _) => ValueTask.FromResult(Relationships.Empty),
                ListResourcesToProcess = _ => ValueTask.FromResult(ImmutableHashSet<ResourceKey>.Empty),
                IsResourceInFileSystem = (_, _) => ValueTask.FromResult(false),
                PutResource = (_, _) => ValueTask.CompletedTask,
                DeleteResource = (_, _) => ValueTask.CompletedTask
            });
    }
}