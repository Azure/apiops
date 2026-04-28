using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

internal sealed class BuildResourceMapTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_parsed_resources_grouped_by_resource_type()
    {
        var gen = from firstKey in Generator.ResourceKey
                  from secondKey in Generator.ResourceKey
                  from fixture in Fixture.Generate()
                  let firstFile = new FileInfo("first.json")
                  let secondFile = new FileInfo("second.json")
                  let ignoredFile = new FileInfo("ignored.json")
                  let fileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => [firstFile, secondFile, ignoredFile]
                  }
                  select (firstKey, secondKey, fileOperations, fixture with
                  {
                      ParseResourceFile = async (file, _, _) =>
                      {
                          await ValueTask.CompletedTask;

                          if (file.FullName == firstFile.FullName)
                          {
                              return firstKey;
                          }

                          if (file.FullName == secondFile.FullName)
                          {
                              return secondKey;
                          }

                          return Option<ResourceKey>.None();
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (firstKey, secondKey, fileOperations, fixture) = tuple;
            var buildResourceMap = fixture.Resolve();

            // Act
            var resources = await buildResourceMap(fileOperations, CancellationToken);

            // Assert
            await Assert.That(resources[firstKey.Resource])
                        .Contains(firstKey);

            await Assert.That(resources[secondKey.Resource])
                        .Contains(secondKey);
        });
    }

    private sealed record Fixture
    {
        public required ParseResourceFile ParseResourceFile { get; init; }

        public BuildResourceMap Resolve()
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton(ParseResourceFile);

            RelationshipsModule.ConfigureBuildResourceMap(builder);

            using var host = builder.Build();

            return host.Services.GetRequiredService<BuildResourceMap>();
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
                ParseResourceFile = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option<ResourceKey>.None();
                }
            });
    }
}

internal sealed class GetRelationshipPairsTests
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
                  let resources = RelationshipTestData.ToResourceMap([childKey])
                  select (parentKey, childKey, resources, Common.NoOpFileOperations, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (parentKey, childKey, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that the parent is a predecessor of the child
            await Assert.That(relationships.Predecessors[childKey])
                        .Contains(parentKey);
        });
    }

    [Test]
    public async Task Adds_relationship_between_api_and_api_operation_policy()
    {
        var gen = from apiKey in Generator.GenerateResourceKey(ApiResource.Instance)
                  from operationKey in from operationName in Generator.ResourceName
                                       select new ResourceKey
                                       {
                                           Resource = ApiOperationResource.Instance,
                                           Name = operationName,
                                           Parents = ParentChain.From([.. apiKey.Parents, (apiKey.Resource, apiKey.Name)])
                                       }
                  let policyKey = new ResourceKey
                  {
                      Resource = ApiOperationPolicyResource.Instance,
                      Name = ResourceName.From("policy").IfErrorThrow(),
                      Parents = ParentChain.From([.. operationKey.Parents, (operationKey.Resource, operationKey.Name)])
                  }
                  from fixture in Fixture.Generate()
                  let resources = RelationshipTestData.ToResourceMap([policyKey])
                  select (apiKey, policyKey, resources, Common.NoOpFileOperations, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (apiKey, policyKey, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that the API is a predecessor of the policy
            await Assert.That(relationships.Predecessors[policyKey])
                        .Contains(apiKey);
        });
    }

    [Test]
    public async Task Adds_relationship_between_workspace_api_and_workspace_api_operation_policy()
    {
        var gen = from apiKey in Generator.GenerateResourceKey(WorkspaceApiResource.Instance)
                  from operationKey in from operationName in Generator.ResourceName
                                       select new ResourceKey
                                       {
                                           Resource = WorkspaceApiOperationResource.Instance,
                                           Name = operationName,
                                           Parents = ParentChain.From([.. apiKey.Parents, (apiKey.Resource, apiKey.Name)])
                                       }
                  let policyKey = new ResourceKey
                  {
                      Resource = WorkspaceApiOperationPolicyResource.Instance,
                      Name = ResourceName.From("policy").IfErrorThrow(),
                      Parents = ParentChain.From([.. operationKey.Parents, (operationKey.Resource, operationKey.Name)])
                  }
                  from fixture in Fixture.Generate()
                  let resources = RelationshipTestData.ToResourceMap([policyKey])
                  select (apiKey, policyKey, resources, Common.NoOpFileOperations, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (apiKey, policyKey, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that the API is a predecessor of the policy
            await Assert.That(relationships.Predecessors[policyKey])
                        .Contains(apiKey);
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
                  let resources = RelationshipTestData.ToResourceMap([compositeKey])
                  select (primaryKey, secondaryKey, compositeKey, resources, fileOperations, fixture with
                  {
                      GetInformationFileDto = async (resource, name, parents, readFile, getSubDirectories, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;

                          var resourceKey = ResourceKey.From(resource, name, parents);

                          return resourceKey == compositeKey
                                    ? dto
                                    : Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (primaryKey, secondaryKey, compositeKey, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that the composite resource depends on its primary and secondary resources
            await Assert.That(relationships.Predecessors[compositeKey])
                        .Contains(primaryKey);

            await Assert.That(relationships.Predecessors[compositeKey])
                        .Contains(secondaryKey);
        });
    }

    [Test]
    public async Task Returns_named_value_relationships_for_named_value_references()
    {
        var gen = from fixture in Fixture.Generate()
                  from resourceKey in Generator.GenerateResourceKey(resource => resource is IResourceWithInformationFile
                                                                                and not IResourceWithReference
                                                                                and not ICompositeResource
                                                                                and not IPolicyResource)
                  from namedValueName in Generator.ResourceName
                  let namedValueKey = ResourceKey.From(NamedValueResource.Instance, namedValueName)
                  let resourceFile = new FileInfo("resource.json")
                  let namedValueFile = new FileInfo("namedValue.json")
                  let fileOperations = Common.NoOpFileOperations with
                  {
                      EnumerateServiceDirectoryFiles = () => [resourceFile, namedValueFile]
                  }
                  let dto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["description"] = $"uses {{{{{namedValueName}}}}}"
                      }
                  }
                  let namedValueDto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["displayName"] = namedValueName.ToString()
                      }
                  }
                  let resources = RelationshipTestData.ToResourceMap([resourceKey, namedValueKey])
                  select (namedValueKey, resourceKey, resources, fileOperations, fixture with
                  {
                      GetInformationFileDto = async (resource, name, parents, readFile, getSubDirectories, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = ResourceKey.From(resource, name, parents);

                          return key == resourceKey
                                    ? dto
                                    : key == namedValueKey
                                        ? namedValueDto
                                        : Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (namedValueKey, resourceKey, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert
            await Assert.That(relationships.Predecessors[resourceKey])
                        .Contains(namedValueKey);
        });
    }

    [Test]
    public async Task Returns_policy_to_named_value_relationships()
    {
        var gen = from fixture in Fixture.Generate()
                  from namedValueKey in Generator.GenerateResourceKey(NamedValueResource.Instance)
                  from policyKey in Generator.GenerateResourceKey(resource => resource is IPolicyResource
                                                                                 and not PolicyFragmentResource
                                                                                 and not WorkspacePolicyFragmentResource)
                  let policyContent = $"<policies><inbound><set-header name=\"x\" exists-action=\"override\"><value>{{{{{namedValueKey.Name}}}}}</value></set-header><base /></inbound></policies>"
                  let namedValueDto = new JsonObject
                  {
                      ["properties"] = new JsonObject
                      {
                          ["displayName"] = namedValueKey.Name.ToString()
                      }
                  }
                  let resources = RelationshipTestData.ToResourceMap([policyKey, namedValueKey])
                  select (namedValueKey, policyKey, policyContent, resources, Common.NoOpFileOperations, fixture with
                  {
                      GetPolicyFileContents = async (resource, name, parents, readFile, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = ResourceKey.From(resource, name, parents);

                          return key == policyKey
                                    ? BinaryData.FromString(policyContent)
                                    : Option.None;
                      },
                      GetInformationFileDto = async (resource, name, parents, readFile, getSubDirectories, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = ResourceKey.From(resource, name, parents);

                          return key == namedValueKey
                                    ? namedValueDto
                                    : Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (namedValueKey, policyKey, policyContent, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that the named value is a predecessor of the policy
            await Assert.That(relationships.Predecessors[policyKey])
                        .Contains(namedValueKey);
        });
    }

    [Test]
    public async Task Returns_policy_to_policy_fragment_relationships()
    {
        var gen = from fixture in Fixture.Generate()
                  from fragmentKey in Generator.GenerateResourceKey(PolicyFragmentResource.Instance)
                  from policyKey in Generator.GenerateResourceKey(resource => resource is IPolicyResource
                                                                                 and not PolicyFragmentResource
                                                                                 and not WorkspacePolicyFragmentResource)
                  let policyContent = $"<policies><inbound><include-fragment fragment-id=\"{fragmentKey.Name}\" /><base /></inbound></policies>"
                  let resources = RelationshipTestData.ToResourceMap([policyKey, fragmentKey])
                  select (fragmentKey, policyKey, policyContent, resources, Common.NoOpFileOperations, fixture with
                  {
                      GetPolicyFileContents = async (resource, name, parents, readFile, cancellationToken) =>
                      {
                          await ValueTask.CompletedTask;

                          var key = ResourceKey.From(resource, name, parents);

                          return key == policyKey
                                    ? BinaryData.FromString(policyContent)
                                    : Option.None;
                      }
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (fragmentKey, policyKey, policyContent, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert that the policy fragment is a predecessor of the policy
            await Assert.That(relationships.Predecessors[policyKey])
                        .Contains(fragmentKey);
        });
    }

    [Test]
    public async Task Returns_a_missing_parent_relationship_for_a_child_resource()
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
                  let resources = RelationshipTestData.ToResourceMap([childKey])
                  select (missingParentKey, childKey, resources, Common.NoOpFileOperations, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (missingParentKey, childKey, resources, fileOperations, fixture) = tuple;
            var getRelationshipPairs = fixture.Resolve();

            // Act
            var pairs = await getRelationshipPairs(resources, fileOperations, CancellationToken);
            var relationships = Relationships.From(pairs, CancellationToken);

            // Assert
            await Assert.That(relationships.Predecessors[childKey])
                        .Contains(missingParentKey);
        });
    }

    private sealed record Fixture
    {
        public required GetInformationFileDto GetInformationFileDto { get; init; }
        public required GetPolicyFileContents GetPolicyFileContents { get; init; }
        public required GetConfigurationOverride GetConfigurationOverride { get; init; }

        public GetRelationshipPairs Resolve()
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton(GetInformationFileDto)
                            .AddSingleton(GetPolicyFileContents)
                            .AddSingleton(GetConfigurationOverride);

            RelationshipsModule.ConfigureGetRelationshipPairs(builder);

            using var host = builder.Build();

            return host.Services.GetRequiredService<GetRelationshipPairs>();
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
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
                GetConfigurationOverride = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                    return Option<JsonObject>.None();
                }
            });
    }
}

internal sealed class ValidateRelationshipGraphTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Throws_when_a_predecessor_is_missing_and_validation_is_strict()
    {
        var gen = from childKey in Generator.GenerateResourceKey(resource => resource is IChildResource
                                                                                and not IResourceWithReference
                                                                                and not ICompositeResource
                                                                                and not IPolicyResource)
                  let parentResource = childKey.Parents.Last().Resource
                  where parentResource is not (GroupResource or ApiOperationResource or WorkspaceResource or WorkspaceGroupResource or WorkspaceApiOperationResource)
                  from fixture in Fixture.Generate()
                  let childResource = (IChildResource)childKey.Resource
                  let missingParentKey = new ResourceKey
                  {
                      Resource = childResource.Parent,
                      Name = childKey.Parents.Last().Name,
                      Parents = ParentChain.From(childKey.Parents.SkipLast(1))
                  }
                  let relationships = Relationships.From([(missingParentKey, childKey)], CancellationToken)
                  let resources = RelationshipTestData.ToResourceMap([childKey])
                  select (relationships, resources, fixture with
                  {
                      IsValidationStrict = () => true
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (relationships, resources, fixture) = tuple;
            var validateRelationshipGraph = fixture.Resolve();

            // Assert that an exception is thrown
            await Assert.That(() => validateRelationshipGraph(relationships, resources, CancellationToken))
                        .Throws<InvalidOperationException>();
        });
    }

    [Test]
    public async Task Does_not_throw_when_a_predecessor_is_missing_and_validation_is_not_strict()
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
                  let relationships = Relationships.From([(missingParentKey, childKey)], CancellationToken)
                  let resources = RelationshipTestData.ToResourceMap([childKey])
                  select (childKey, resources, relationships, fixture with
                  {
                      IsValidationStrict = () => false
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (childKey, resources, relationships, fixture) = tuple;
            var validateRelationshipGraph = fixture.Resolve();

            // Act
            validateRelationshipGraph(relationships, resources, CancellationToken);

            // Assert
            await Assert.That(resources[childKey.Resource])
                        .Contains(childKey);
        });
    }

    private sealed record Fixture
    {
        public required IsValidationStrict IsValidationStrict { get; init; }

        public ValidateRelationshipGraph Resolve()
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton(IsValidationStrict)
                            .AddNullLogger();

            RelationshipsModule.ConfigureValidateRelationshipGraph(builder);

            using var host = builder.Build();

            return host.Services.GetRequiredService<ValidateRelationshipGraph>();
        }

        public static Gen<Fixture> Generate() =>
            from isStrict in Gen.Bool
            select new Fixture
            {
                IsValidationStrict = () => isStrict
            };
    }
}

internal sealed class GetRelationshipsTests
{
    private static CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? CancellationToken.None;

    [Test]
    public async Task Returns_relationships_from_the_built_pairs_and_validates_them()
    {
        var gen = from expected in Common.GenerateRelationships()
                  from fixture in Fixture.Generate()
                  let fileOperations = Common.NoOpFileOperations
                  let resources = RelationshipTestData.ToResourceMap(RelationshipTestData.GetNodes(expected))
                  let pairs = RelationshipTestData.ToPairs(expected)
                  select (expected, resources, pairs, fileOperations, fixture);

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (expected, resources, pairs, fileOperations, fixture) = tuple;
            FileOperations? buildResourceMapFileOperations = null;
            ImmutableDictionary<IResource, ImmutableHashSet<ResourceKey>>? getRelationshipPairsResources = null;
            FileOperations? getRelationshipPairsFileOperations = null;
            Relationships? validatedRelationships = null;
            ImmutableDictionary<IResource, ImmutableHashSet<ResourceKey>>? validatedResources = null;

            var getRelationships = (fixture with
            {
                BuildResourceMap = async (receivedFileOperations, cancellationToken) =>
                {
                    buildResourceMapFileOperations = receivedFileOperations;
                    await ValueTask.CompletedTask;
                    return resources;
                },
                GetRelationshipPairs = async (receivedResources, receivedFileOperations, cancellationToken) =>
                {
                    getRelationshipPairsResources = receivedResources;
                    getRelationshipPairsFileOperations = receivedFileOperations;
                    await ValueTask.CompletedTask;
                    return pairs;
                },
                ValidateRelationshipGraph = (relationships, receivedResources, cancellationToken) =>
                {
                    validatedRelationships = relationships;
                    validatedResources = receivedResources;
                }
            }).Resolve();

            // Act
            var relationships = await getRelationships(fileOperations, CancellationToken);
            var actualPairs = RelationshipTestData.ToPairs(relationships);
            var validatedPairs = validatedRelationships is null
                                    ? ImmutableHashSet<(ResourceKey Predecessor, ResourceKey Successor)>.Empty
                                    : RelationshipTestData.ToPairs(validatedRelationships);

            // Assert
            await Assert.That(actualPairs.Count)
                        .IsEqualTo(pairs.Count);

            await Assert.That(pairs)
                        .All(pair => actualPairs.Contains(pair));

            await Assert.That(buildResourceMapFileOperations)
                        .IsEqualTo(fileOperations);

            await Assert.That(getRelationshipPairsResources)
                        .IsEqualTo(resources);

            await Assert.That(getRelationshipPairsFileOperations)
                        .IsEqualTo(fileOperations);

            await Assert.That(validatedPairs.Count)
                        .IsEqualTo(pairs.Count);

            await Assert.That(pairs)
                        .All(pair => validatedPairs.Contains(pair));

            await Assert.That(validatedResources)
                        .IsEqualTo(resources);
        });
    }

    [Test]
    public async Task Propagates_validation_failures()
    {
        var gen = from fixture in Fixture.Generate()
                  select fixture with
                  {
                      ValidateRelationshipGraph = (relationships, resources, cancellationToken) =>
                          throw new InvalidOperationException("validation failed")
                  };

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var getRelationships = fixture.Resolve();

            // Assert that an exception is thrown
            await Assert.That(async () => await getRelationships(Common.NoOpFileOperations, CancellationToken))
                        .Throws<InvalidOperationException>();
        });
    }

    private sealed record Fixture
    {
        public required BuildResourceMap BuildResourceMap { get; init; }
        public required GetRelationshipPairs GetRelationshipPairs { get; init; }
        public required ValidateRelationshipGraph ValidateRelationshipGraph { get; init; }

        public GetRelationships Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton(BuildResourceMap)
                    .AddSingleton(GetRelationshipPairs)
                    .AddSingleton(ValidateRelationshipGraph);

            using var provider = services.BuildServiceProvider();

            return RelationshipsModule.ResolveGetRelationships(provider);
        }

        public static Gen<Fixture> Generate() =>
            from relationships in Common.GenerateRelationships()
            let resources = RelationshipTestData.ToResourceMap(RelationshipTestData.GetNodes(relationships))
            let pairs = RelationshipTestData.ToPairs(relationships)
            select new Fixture
            {
                BuildResourceMap = async (_, _) =>
                {
                    await ValueTask.CompletedTask;
                    return resources;
                },
                GetRelationshipPairs = async (_, _, _) =>
                {
                    await ValueTask.CompletedTask;
                    return pairs;
                },
                ValidateRelationshipGraph = (relationships, resources, cancellationToken) => { }
            };
    }
}

internal static class RelationshipTestData
{
    public static ImmutableHashSet<ResourceKey> GetNodes(Relationships relationships) =>
        [.. relationships.Predecessors.Keys,
          .. relationships.Predecessors.SelectMany(kvp => kvp.Value),
          .. relationships.Successors.Keys,
          .. relationships.Successors.SelectMany(kvp => kvp.Value)];

    public static ImmutableHashSet<(ResourceKey Predecessor, ResourceKey Successor)> ToPairs(Relationships relationships) =>
        [.. relationships.Predecessors.SelectMany(kvp => kvp.Value.Select(predecessor => (predecessor, kvp.Key)))];

    public static ImmutableDictionary<IResource, ImmutableHashSet<ResourceKey>> ToResourceMap(IEnumerable<ResourceKey> keys) =>
        [.. keys.GroupBy(key => key.Resource)
                .Select(group => KeyValuePair.Create(group.Key, group.ToImmutableHashSet()))];
}