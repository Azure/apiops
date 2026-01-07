using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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

internal sealed class GetPreviousRelationshipsTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_an_empty_set_when_a_commit_id_is_not_provided()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      CommitIdWasPassed = () => false
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getPreviousRelationships = fixture.Resolve();

            // Act
            var relationships = await getPreviousRelationships(CancellationToken);

            // Assert that an empty set of relationships is returned
            await Assert.That(relationships)
                        .IsEqualTo(Relationships.Empty);
        });
    }

    [Test]
    public async Task Returns_an_empty_set_when_file_operations_for_the_previous_commit_are_unavailable()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      CommitIdWasPassed = () => true,
                      GetPreviousCommitFileOperations = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getPreviousRelationships = fixture.Resolve();

            // Act
            var relationships = await getPreviousRelationships(CancellationToken);

            // Assert that an empty set of relationships is returned
            await Assert.That(relationships)
                        .IsEqualTo(Relationships.Empty);
        });
    }

    [Test]
    public async Task Uses_file_operations_from_the_previous_commit_when_a_commit_id_is_provided()
    {
        var gen = from expected in Common.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  select (expected, fixture with
                  {
                      CommitIdWasPassed = () => true,
                      GetPreviousCommitFileOperations = () => Common.NoOpFileOperations,
                      GetRelationships = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return expected;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (expected, fixture) = tuple;
            var getPreviousRelationships = fixture.Resolve();

            // Act
            var relationships = await getPreviousRelationships(CancellationToken);

            // Assert that the expected relationships are returned
            await Assert.That(relationships)
                        .IsEqualTo(expected);
        });
    }

    private sealed record Fixture
    {
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required GetPreviousCommitFileOperations GetPreviousCommitFileOperations { get; init; }
        public required GetRelationships GetRelationships { get; init; }

        public GetPreviousRelationships Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(CommitIdWasPassed)
                    .AddSingleton(GetPreviousCommitFileOperations)
                    .AddSingleton(GetRelationships);

            using var provider = services.BuildServiceProvider();

            return RelationshipsModule.ResolveGetPreviousRelationships(provider);
        }

        public static Gen<Fixture> Generate() =>
            from wasCommitPassed in Gen.Bool
            from fileOperationsOption in Gen.Const(Common.NoOpFileOperations).OptionOf()
            from relationships in Common.GenerateRelationships()
            select new Fixture
            {
                CommitIdWasPassed = () => wasCommitPassed,
                GetPreviousCommitFileOperations = () => fileOperationsOption,
                GetRelationships = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                    return relationships;
                }
            };
    }
}

internal sealed class GetCurrentRelationshipsTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Uses_local_file_operations_when_a_commit_id_is_not_provided()
    {
        var gen = from localRelationships in Common.GenerateRelationships()
                  from commitRelationships in Common.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  let dummyFile = new FileInfo("dummy.txt")
                  let localFileOperations = Common.NoOpFileOperations with
                  {
                      ReadFile = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return BinaryData.FromString("LOCAL");
                      }
                  }
                  let commitFileOperations = Common.NoOpFileOperations with
                  {
                      ReadFile = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return BinaryData.FromString("COMMIT");
                      }
                  }
                  select (localRelationships, fixture with
                  {
                      CommitIdWasPassed = () => false,
                      GetLocalFileOperations = () => localFileOperations,
                      GetCurrentCommitFileOperations = () => Option.Some(commitFileOperations),
                      GetRelationships = async (fileOperations, cancellationToken) =>
                      {
                          var contentsOption = await fileOperations.ReadFile(dummyFile, cancellationToken);

                          return contentsOption.Map(contents => contents.ToString() switch
                          {
                              "LOCAL" => localRelationships,
                              "COMMIT" => commitRelationships,
                              _ => Relationships.Empty
                          }).IfNone(() => Relationships.Empty);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (localRelationships, fixture) = tuple;
            var getCurrentRelationships = fixture.Resolve();

            // Act
            var relationships = await getCurrentRelationships(CancellationToken);

            // Assert that we return the local relationships
            await Assert.That(relationships)
                        .IsEqualTo(localRelationships);
        });
    }

    [Test]
    public async Task Uses_current_commit_file_operations_when_a_commit_id_is_provided()
    {
        var gen = from localRelationships in Common.GenerateRelationships()
                  from commitRelationships in Common.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  let dummyFile = new FileInfo("dummy.txt")
                  let localFileOperations = Common.NoOpFileOperations with
                  {
                      ReadFile = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return BinaryData.FromString("LOCAL");
                      }
                  }
                  let commitFileOperations = Common.NoOpFileOperations with
                  {
                      ReadFile = async (_, _) =>
                      {
                          await ValueTask.CompletedTask;
                          return BinaryData.FromString("COMMIT");
                      }
                  }
                  select (commitRelationships, fixture with
                  {
                      CommitIdWasPassed = () => true,
                      GetLocalFileOperations = () => localFileOperations,
                      GetCurrentCommitFileOperations = () => Option.Some(commitFileOperations),
                      GetRelationships = async (fileOperations, cancellationToken) =>
                      {
                          var contentsOption = await fileOperations.ReadFile(dummyFile, cancellationToken);

                          return contentsOption.Map(contents => contents.ToString() switch
                          {
                              "LOCAL" => localRelationships,
                              "COMMIT" => commitRelationships,
                              _ => Relationships.Empty
                          }).IfNone(() => Relationships.Empty);
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (commitRelationships, fixture) = tuple;
            var getCurrentRelationships = fixture.Resolve();

            // Act
            var relationships = await getCurrentRelationships(CancellationToken);

            // Assert that we return the commit relationships
            await Assert.That(relationships)
                        .IsEqualTo(commitRelationships);
        });
    }

    [Test]
    public async Task Throws_when_file_operations_for_the_current_commit_are_unavailable()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      CommitIdWasPassed = () => true,
                      GetCurrentCommitFileOperations = () => Option.None
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getCurrentRelationships = fixture.Resolve();

            // Assert that an exception is thrown
            await Assert.That(async () => await getCurrentRelationships(CancellationToken))
                        .Throws<InvalidOperationException>();
        });
    }

    private sealed record Fixture
    {
        public required CommitIdWasPassed CommitIdWasPassed { get; init; }
        public required GetCurrentCommitFileOperations GetCurrentCommitFileOperations { get; init; }
        public required GetLocalFileOperations GetLocalFileOperations { get; init; }
        public required GetRelationships GetRelationships { get; init; }

        public GetCurrentRelationships Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(CommitIdWasPassed)
                    .AddSingleton(GetCurrentCommitFileOperations)
                    .AddSingleton(GetLocalFileOperations)
                    .AddSingleton(GetRelationships);

            using var provider = services.BuildServiceProvider();

            return RelationshipsModule.ResolveGetCurrentRelationships(provider);
        }

        public static Gen<Fixture> Generate() =>
            from wasCommitPassed in Gen.Bool
            from currentCommitFileOperationsOption in Gen.Const(Common.NoOpFileOperations).OptionOf()
            from relationships in Common.GenerateRelationships()
            select new Fixture
            {
                CommitIdWasPassed = () => wasCommitPassed,
                GetCurrentCommitFileOperations = () => currentCommitFileOperationsOption,
                GetLocalFileOperations = () => Common.NoOpFileOperations,
                GetRelationships = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                    return relationships;
                }
            };
    }
}

internal sealed class IsValidationStrictTests
{
    [Test]
    public async Task Returns_false_when_the_setting_is_not_present()
    {
        var gen = from fixture in Fixture.Generate()
                  let configuration = new ConfigurationBuilder().AddInMemoryCollection([])
                                                                .Build()
                  select fixture with
                  {
                      Configuration = configuration
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var isValidationStrict = fixture.Resolve();

            // Act
            var result = isValidationStrict();

            // Assert that validation is not strict
            await Assert.That(result)
                        .IsFalse();
        });
    }

    [Test]
    public async Task Returns_the_configuration_setting_when_it_is_a_boolean()
    {
        var gen = from expected in Gen.Bool
                  from setting in boolToString(expected)
                  from fixture in Fixture.Generate()
                  let configuration =
                    new ConfigurationBuilder()
                        .AddInMemoryCollection([KeyValuePair.Create("STRICT_VALIDATION", (string?)setting)])
                        .Build()
                  select (expected, fixture with
                  {
                      Configuration = configuration
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (expected, fixture) = tuple;
            var isValidationStrict = fixture.Resolve();

            // Act
            var result = isValidationStrict();

            // Assert that the result matches the expected value
            await Assert.That(result)
                        .IsEqualTo(expected);
        });

        static Gen<string> boolToString(bool input) =>
            from characters in Generator.Traverse(input ? "true" : "false",
                                                  c => Gen.OneOfConst(char.ToUpperInvariant(c), char.ToLowerInvariant(c)))
            select new string([.. characters]);
    }

    [Test]
    public async Task Throws_when_the_setting_is_present_but_not_a_boolean()
    {
        var gen = from setting in Gen.String
                  where bool.TryParse(setting, out _) is false
                  where string.IsNullOrEmpty(setting) is false // bool.TryParse treats an empty string as false
                  from fixture in Fixture.Generate()
                  let configuration =
                    new ConfigurationBuilder()
                        .AddInMemoryCollection([KeyValuePair.Create("STRICT_VALIDATION", (string?)setting)])
                        .Build()
                  select fixture with
                  {
                      Configuration = configuration
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var isValidationStrict = fixture.Resolve();

            // Assert that an exception is thrown
            await Assert.That(() => isValidationStrict())
                        .Throws<InvalidOperationException>();
        });
    }

    private sealed record Fixture
    {
        public required IConfiguration Configuration { get; init; }

        public IsValidationStrict Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(Configuration);

            using var provider = services.BuildServiceProvider();

            return RelationshipsModule.ResolveIsValidationStrict(provider);
        }

        public static Gen<Fixture> Generate() =>
            from configuration in Gen.Const(new ConfigurationBuilder().Build())
            select new Fixture
            {
                Configuration = configuration
            };
    }
}

internal sealed class GetRelationshipsTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_a_parent_to_child_relationship_for_a_child_resource()
    {
        var gen = from childKey in Generator.GenerateResourceKey(resource => resource is IChildResource
                                                                                and not IResourceWithReference
                                                                                and not ICompositeResource
                                                                                and not IPolicyResource)
                  from fixture in Fixture.Generate()
                  let childResource = (IChildResource)childKey.Resource
                  let parentKey = new ResourceKey
                  {
                      Resource = childResource.Parent,
                      Name = childKey.Parents.Last().Name,
                      Parents = ParentChain.From(childKey.Parents.SkipLast(1))
                  }
                  let childFile = new FileInfo("child.json")
                  let fileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => [childFile]
                  }
                  select (parentKey, childKey, fileOperations, fixture with
                  {
                      IsValidationStrict = () => false,
                      ParseResourceFile = async (file, _, _) =>
                      {
                          await ValueTask.CompletedTask;

                          if (file.FullName == childFile.FullName)
                          {
                              return childKey;
                          }
                          else
                          {
                              return Option.None;
                          }
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (parentKey, childKey, fileOperations, fixture) = tuple;
            var getRelationships = fixture.Resolve();

            // Act
            var relationships = await getRelationships(fileOperations, CancellationToken);

            // Assert that the parent is a predecessor of the child
            await Assert.That(relationships.Predecessors[childKey])
                        .Contains(parentKey);
        });
    }

    [Test]
    public async Task Returns_primary_and_secondary_relationships_for_a_composite_resource()
    {
        var gen = from compositeKey in Generator.GenerateResourceKey(resource => resource is ICompositeResource)
                  let compositeResource = (ICompositeResource)compositeKey.Resource
                  from secondaryName in compositeResource is ILinkResource
                                           ? Generator.ResourceName
                                           : Gen.Const(compositeKey.Name)
                  let primaryKey = new ResourceKey
                  {
                      Name = compositeKey.Parents.Last().Name,
                      Resource = compositeResource.Primary,
                      Parents = ParentChain.From(compositeKey.Parents.SkipLast(1))
                  }
                  let secondaryKey = new ResourceKey
                  {
                      Name = secondaryName,
                      Resource = compositeResource.Secondary,
                      Parents = ParentChain.From(compositeKey.Parents.SkipLast(1))
                  }
                  from fixture in Fixture.Generate()
                  let compositeFile = new FileInfo("composite.json")
                  let fileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => [compositeFile]
                  }
                  let dto = compositeResource switch
                  {
                      ILinkResource linkResource => new JsonObject
                      {
                          ["properties"] = new JsonObject
                          {
                              [linkResource.DtoPropertyNameForLinkedResource] = secondaryKey.ToString()
                          }
                      },
                      _ => new JsonObject()
                  }
                  select (primaryKey, secondaryKey, compositeKey, dto, fileOperations, fixture with
                  {
                      IsValidationStrict = () => false,
                      ParseResourceFile = async (file, _, _) =>
                      {
                          await ValueTask.CompletedTask;

                          if (file.FullName == compositeFile.FullName)
                          {
                              return compositeKey;
                          }
                          else
                          {
                              return Option.None;
                          }
                      },
                      GetInformationFileDto = async (resource, name, parents, readFile, getSubDirectories, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;

                          var resourceKey = new ResourceKey
                          {
                              Resource = resource,
                              Name = name,
                              Parents = parents
                          };

                          return resourceKey == compositeKey
                                    ? dto
                                    : Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (primaryKey, secondaryKey, compositeKey, dto, fileOperations, fixture) = tuple;
            var getRelationships = fixture.Resolve();

            // Act
            var relationships = await getRelationships(fileOperations, CancellationToken);

            // Assert that the composite resource depends on its primary and secondary resources
            await Assert.That(relationships.Predecessors[compositeKey])
                        .Contains(primaryKey);

            await Assert.That(relationships.Predecessors[compositeKey])
                        .Contains(secondaryKey);
        });
    }

    [Test]
    public async Task Throws_when_a_predecessor_is_missing_and_validation_is_strict()
    {
        var gen = from childKey in Generator.GenerateResourceKey(resource => resource is IChildResource
                                                                                and not IResourceWithReference
                                                                                and not ICompositeResource
                                                                                and not IPolicyResource)
                  // Skip parents that should not be validated
                  let parentResource = childKey.Parents.Last().Resource
                  where parentResource is not (GroupResource or ApiOperationResource or WorkspaceResource or WorkspaceGroupResource or WorkspaceApiOperationResource)
                  from fixture in Fixture.Generate()
                  let childFile = new FileInfo("child.json")
                  let fileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => [childFile]
                  }
                  select (fileOperations, fixture with
                  {
                      IsValidationStrict = () => true,
                      ParseResourceFile = async (file, _, _) =>
                      {
                          await ValueTask.CompletedTask;

                          if (file.FullName == childFile.FullName)
                          {
                              return childKey;
                          }
                          else
                          {
                              return Option.None;
                          }
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (fileOperations, fixture) = tuple;
            var getRelationships = fixture.Resolve();

            // Assert that an exception is thrown
            await Assert.That(async () => await getRelationships(fileOperations, CancellationToken))
                        .Throws<InvalidOperationException>();
        });
    }

    [Test]
    public async Task Returns_relationships_when_a_predecessor_is_missing_and_validation_is_not_strict()
    {
        var gen = from childKey in Generator.GenerateResourceKey(resource => resource is IChildResource
                                                                                and not IResourceWithReference
                                                                                and not ICompositeResource
                                                                                and not IPolicyResource)
                  from fixture in Fixture.Generate()
                  let childResource = (IChildResource)childKey.Resource
                  let missingParentKey = new ResourceKey
                  {
                      Resource = childResource.Parent,
                      Name = childKey.Parents.Last().Name,
                      Parents = ParentChain.From(childKey.Parents.SkipLast(1))
                  }
                  let childFile = new FileInfo("child.json")
                  let fileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => [childFile]
                  }
                  select (missingParentKey, childKey, fileOperations, fixture with
                  {
                      IsValidationStrict = () => false,
                      ParseResourceFile = async (file, _, _) =>
                      {
                          await ValueTask.CompletedTask;

                          if (file.FullName == childFile.FullName)
                          {
                              return childKey;
                          }
                          else
                          {
                              return Option.None;
                          }
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (missingParentKey, childKey, fileOperations, fixture) = tuple;
            var getRelationships = fixture.Resolve();

            // Act
            var relationships = await getRelationships(fileOperations, CancellationToken);

            // Assert that the missing predecessor relationship is still returned
            await Assert.That(relationships.Predecessors[childKey])
                        .Contains(missingParentKey);
        });
    }

    private sealed record Fixture
    {
        public required ParseResourceFile ParseResourceFile { get; init; }
        public required GetInformationFileDto GetInformationFileDto { get; init; }
        public required GetPolicyFileContents GetPolicyFileContents { get; init; }
        public required IsValidationStrict IsValidationStrict { get; init; }

        public GetRelationships Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(ParseResourceFile)
                    .AddSingleton(GetInformationFileDto)
                    .AddSingleton(GetPolicyFileContents)
                    .AddSingleton(IsValidationStrict)
                    .AddNullLogger();

            using var provider = services.BuildServiceProvider();

            return RelationshipsModule.ResolveGetRelationships(provider);
        }

        public static Gen<Fixture> Generate() =>
            from isStrict in Gen.Bool
            select new Fixture
            {
                ParseResourceFile = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option<ResourceKey>.None();
                },
                GetInformationFileDto = async (_, _, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option<JsonObject>.None();
                },
                GetPolicyFileContents = async (_, _, _, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option<BinaryData>.None();
                },
                IsValidationStrict = () => isStrict,
            };
    }
}