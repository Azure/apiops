using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace extractor.tests;

internal sealed class ResourceIsInConfigurationTests
{
    private static System.Threading.CancellationToken CancellationToken =>
        TestContext.Current?.Execution.CancellationToken ?? System.Threading.CancellationToken.None;

    [Test]
    public async Task Key_with_missing_resource_returns_none()
    {
        var gen = // Generate configuration keys
                  from fixture in Fixture.Generate()
                  from keys in Generator.ResourceKeys
                  let configuration = ResourceKeysToConfiguration(keys)
                  // Generate a key whose resource is not in configuration
                  from key in Generator.ResourceKey
                  let configurationResources = keys.Select(key => key.Resource).ToImmutableHashSet()
                  where configurationResources.Contains(key.Resource) is false
                  select (key, fixture with
                  {
                      Configuration = configuration
                  });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (key, fixture) = tuple;
            var resourceIsInConfiguration = fixture.Resolve();

            // Act
            var result = await resourceIsInConfiguration(key, CancellationToken);

            // Assert
            await Assert.That(result).IsNone();
        });
    }

    [Test]
    public async Task Key_with_missing_parent_returns_none()
    {
        var gen =
            // Generate configuration keys
            from fixture in Fixture.Generate()
            from keys in Generator.ResourceKeys
            let configuration = ResourceKeysToConfiguration(keys)
            // Pick a key with a parent
            let keysWithParents = keys.Where(key => key.Parents.Count > 0).ToImmutableArray()
            where keysWithParents.Length > 0
            from keyWithParent in Gen.OneOfConst([.. keysWithParents])
                // Change the parent's name to one that doesn't exist in configuration
            from newParentName in Generator.ResourceName
            let newParentKey = new ResourceKey
            {
                Name = newParentName,
                Parents = ParentChain.From(keyWithParent.Parents.SkipLast(1)),
                Resource = keyWithParent.Parents.Last().Resource
            }
            where keys.Contains(newParentKey) is false
            let key = new ResourceKey
            {
                Name = keyWithParent.Name,
                Resource = keyWithParent.Resource,
                Parents = ParentChain.From([.. newParentKey.Parents, (newParentKey.Resource, newParentKey.Name)])
            }
            select (key, fixture with
            {
                Configuration = configuration
            });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (key, fixture) = tuple;
            var resourceIsInConfiguration = fixture.Resolve();

            // Act
            var result = await resourceIsInConfiguration(key, CancellationToken);

            // Assert
            await Assert.That(result).IsNone();
        });
    }

    [Test]
    public async Task Key_with_existing_resource_and_missing_name_returns_false()
    {
        var gen =
            // Generate configuration keys
            from fixture in Fixture.Generate()
            from keys in Generator.ResourceKeys
            let configuration = ResourceKeysToConfiguration(keys)
            // Pick a key and change its name to one that doesn't exist in configuration
            where keys.Count > 0
            from existingKey in Gen.OneOfConst([.. keys])
            from newName in Generator.ResourceName
            let key = existingKey with { Name = newName }
            where keys.Contains(key) is false
            // Ensure that any API or workspace API in the chain uses a root name
            where HasNoRevisionsInPath(key)
            select (key, fixture with
            {
                Configuration = configuration
            });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (key, fixture) = tuple;
            var resourceIsInConfiguration = fixture.Resolve();

            // Act
            var result = await resourceIsInConfiguration(key, CancellationToken);

            // Assert
            await Assert.That(result)
                        .IsSome()
                        .And
                        .IsFalse();
        });
    }

    /// <summary>
    /// The key and its parents do not contain any API or workspace API revisions.
    /// </summary>
    private static bool HasNoRevisionsInPath(ResourceKey key) =>
        key.AsParentChain()
           .All(tuple => tuple.Resource is not (ApiResource or WorkspaceApiResource)
                         || ApiRevisionModule.IsRootName(tuple.Name));

    [Test]
    public async Task Existing_key_returns_true()
    {
        var gen =
            // Generate configuration keys
            from fixture in Fixture.Generate()
            from keys in Generator.ResourceKeys
            let configuration = ResourceKeysToConfiguration(keys)
            // Pick an existing key
            where keys.Count > 0
            from key in Gen.OneOfConst([.. keys])
                // Ensure that any API or workspace API in the chain uses a root name
            where HasNoRevisionsInPath(key)
            select (key, fixture with
            {
                Configuration = configuration
            });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (key, fixture) = tuple;
            var resourceIsInConfiguration = fixture.Resolve();

            // Act
            var result = await resourceIsInConfiguration(key, CancellationToken);

            // Assert
            await Assert.That(result)
                        .IsSome()
                        .And
                        .IsTrue();
        });
    }

    [Test]
    public async Task Revisioned_api_names_are_resolved_against_root_names()
    {
        var gen =
            // Generate configuration keys, ensuring that there are no API revisions
            from fixture in Fixture.Generate()
            from keys in
                from keys in Generator.ResourceKeys
                select keys.Where(HasNoRevisionsInPath)
                           .ToImmutableHashSet()
            let configuration = ResourceKeysToConfiguration(keys)
            // Pick an existing key
            where keys.Count > 0
            from existingKey in Gen.OneOfConst([.. keys])
                // Ensure that the key has an API in its path
            where existingKey.AsParentChain()
                             .Any(tuple => tuple.Resource is ApiResource or WorkspaceApiResource)
            // Change the API or workspace API name to a revisioned name
            from key in
                from segments in Generator.Traverse(existingKey.AsParentChain(),
                                                    tuple => tuple.Resource is (ApiResource or WorkspaceApiResource)
                                                                ? from revision in Gen.Int[1, 100]
                                                                  let newName = ApiRevisionModule.Combine(tuple.Name, revision)
                                                                  select (tuple.Resource, Name: newName)
                                                                : Gen.Const(tuple))
                select new ResourceKey
                {
                    Name = segments.Last().Name,
                    Resource = segments.Last().Resource,
                    Parents = ParentChain.From(segments.SkipLast(1))
                }
            select (key, fixture with
            {
                Configuration = configuration
            });

        await gen.SampleAsync(async tuple =>
        {
            // Arrange
            var (key, fixture) = tuple;
            var resourceIsInConfiguration = fixture.Resolve();

            // Act
            var result = await resourceIsInConfiguration(key, CancellationToken);

            // Assert
            await Assert.That(result)
                        .IsSome()
                        .And
                        .IsTrue();
        });
    }

    private static IConfiguration ResourceKeysToConfiguration(ICollection<ResourceKey> resourceKeys)
    {
        var rootResources = resourceKeys.Where(key => key.Parents.Count == 0);
        var json = getResourceKeysJson(rootResources);

        using var stream = BinaryData.FromObjectAsJson(json)
                                     .ToStream();

        return new ConfigurationBuilder()
                    .AddJsonStream(stream)
                    .Build();

        JsonNode getResourceKeyJson(ResourceKey resourceKey)
        {
            var successors = resourceKeys.Where(potentialSuccessor => potentialSuccessor.Parents == resourceKey.AsParentChain());

            return successors.ToImmutableArray() switch
            {
                [] => resourceKey.Name.ToString(),
                _ => new JsonObject
                {
                    [resourceKey.Name.ToString()] = getResourceKeysJson(successors)
                }
            };
        }

        JsonObject getResourceKeysJson(IEnumerable<ResourceKey> resourceKeys) =>
            resourceKeys.GroupBy(key => key.Resource)
                        .Aggregate(new JsonObject(),
                                   (jsonObject, group) => jsonObject.SetProperty(group.Key.ConfigurationKey,
                                                                                 new JsonArray([.. group.Select(getResourceKeyJson)])));
    }

    private sealed record Fixture
    {
        public required IConfiguration Configuration { get; init; }

        public ResourceIsInConfiguration Resolve()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(Configuration)
                    .AddTestActivitySource();

            using var provider = services.BuildServiceProvider();

            return ConfigurationModule.ResolveResourceIsInConfiguration(provider);
        }

        public static Gen<Fixture> Generate() =>
            Gen.Const(new Fixture
            {
                Configuration = new ConfigurationBuilder().Build()
            });
    }
}