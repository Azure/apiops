using AwesomeAssertions;
using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace extractor.tests;

public class RunExtractorTests
{
    [Fact]
    public async Task Unsupported_resources_are_not_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var cancellationToken = TestContext.Current.CancellationToken;

            // Act
            await fixture.Run(cancellationToken);

            // Assert
            var extractedResources = fixture.WrittenResourceDtos.Keys.Select(key => key.Resource);
            var supportedResources = fixture.SupportedApimResources;
            extractedResources.Should().BeSubsetOf(supportedResources);
        });
    }

    [Fact]
    public async Task Descendants_of_unsupported_parents_are_not_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var cancellationToken = TestContext.Current.CancellationToken;

            // Act
            await fixture.Run(cancellationToken);

            // Assert
            var extractedResources = fixture.WrittenResourceDtos.Keys.Select(key => key.Resource);
            var extractedResourceAncestors = extractedResources.SelectMany(resource => resource.GetTraversalPredecessorHierarchy());
            var supportedResources = fixture.SupportedApimResources;
            extractedResourceAncestors.Should().BeSubsetOf(supportedResources);
        });
    }

    [Fact]
    public async Task Filtered_out_resources_are_not_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var cancellationToken = TestContext.Current.CancellationToken;

            // Act
            await fixture.Run(cancellationToken);

            // Assert
            var extractedResources = fixture.WrittenResourceDtos.Keys;
            var resourcesToExtract = fixture.ResourcesToExtract;
            extractedResources.Should().BeSubsetOf(resourcesToExtract);
        });
    }

    [Fact]
    public async Task Written_dtos_match_apim_dtos()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var cancellationToken = TestContext.Current.CancellationToken;

            // Act
            await fixture.Run(cancellationToken);

            // Assert
            fixture.WrittenResourceDtos.Should().AllSatisfy(kvp =>
            {
                fixture.ApimResourceDtos
                       .Should().ContainKey(kvp.Key)
                       .WhoseValue
                       .IfNoneNull()
                       .Should().BeEquivalentTo(kvp.Value);
            });
        });
    }

    [Fact]
    public async Task Only_api_releases_of_current_apis_get_extracted()
    {
        var gen = Fixture.Generate();

        await gen.SampleAsync(async fixture =>
        {
            // Arrange
            var cancellationToken = TestContext.Current.CancellationToken;

            // Act
            await fixture.Run(cancellationToken);

            // Assert
            var extractedNonCurrentApiReleases =
                fixture.WrittenResourceDtos.Keys
                       .Where(key => key.Resource is ApiReleaseResource or WorkspaceApiReleaseResource)
                       .Where(key => key.Parents
                                        .Any(x => x.Resource is ApiResource or WorkspaceApiResource
                                                  && ApiRevisionModule.IsRootName(x.Name) is false));
            extractedNonCurrentApiReleases.Should().BeEmpty();
        });
    }

    private sealed record Fixture
    {
        private ImmutableDictionary<ResourceKey, JsonObject> writtenResourceDtos = [];

        private readonly Lazy<ImmutableDictionary<(IResource, ParentChain), ImmutableHashSet<ResourceName>>> resourceNamesDictionary = new();
        private readonly Lazy<ImmutableDictionary<(IResource, ParentChain), ImmutableArray<(ResourceName, JsonObject)>>> resourceDtosDictionary = new();

        public required ImmutableDictionary<ResourceKey, Option<JsonObject>> ApimResourceDtos
        {
            get;
            init
            {
                resourceNamesDictionary = new(() => value.Keys
                                                         .GroupBy(key => (key.Resource, key.Parents))
                                                         .ToImmutableDictionary(group => group.Key,
                                                                                group => group.Select(key => key.Name).ToImmutableHashSet()));

                resourceDtosDictionary = new(() => value.Choose(kvp => from dto in kvp.Value
                                                                       select (Key: (kvp.Key.Resource, kvp.Key.Parents), Value: (kvp.Key.Name, dto)))
                                                        .GroupBy(item => item.Key, item => item.Value)
                                                        .ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray()));

                field = value;
            }
        }

        public required ImmutableHashSet<IResource> SupportedApimResources { get; init; }
        public required ImmutableHashSet<ResourceKey> ResourcesToExtract { get; init; }
        public ImmutableDictionary<ResourceKey, JsonObject> WrittenResourceDtos => writtenResourceDtos;

        public async ValueTask Run(CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();

            services.AddSingleton<ResourceGraph>(ResourceGraph.From(ApimResourceDtos.Keys.Select(key => key.Resource),
                                                 cancellationToken))
                    .AddSingleton<IsResourceSupportedInApim>(async (resource, cancellationToken) =>
                    {
                        await ValueTask.CompletedTask;
                        return SupportedApimResources.Contains(resource);
                    })
                    .AddSingleton<ListResourceNamesFromApim>((resource, parents, cancellationToken) =>
                    {
                        return resourceNamesDictionary.Value
                                                      .Find((resource, parents))
                                                      .IfNone(() => [])
                                                      .ToAsyncEnumerable();
                    })
                    .AddSingleton<ListResourceDtosFromApim>((resource, parents, cancellationToken) =>
                    {
                        return resourceDtosDictionary.Value
                                                     .Find((resource, parents))
                                                     .IfNone(() => [])
                                                     .ToAsyncEnumerable();
                    })
                    .AddSingleton<ShouldExtract>(async (resourceKey, cancellationToken) =>
                    {
                        await ValueTask.CompletedTask;
                        return ResourcesToExtract.Contains(resourceKey);
                    })
                    .AddSingleton<WriteResource>(async (resourceKey, dtoOption, cancellationToken) =>
                    {
                        await ValueTask.CompletedTask;
                        dtoOption.Iter(dto => ImmutableInterlocked.AddOrUpdate(ref writtenResourceDtos, resourceKey, dto, (_, _) => dto));
                    })
                    .AddSingleton<ActivitySource>(provider => new ActivitySource("extractor.tests"))
                    .AddSingleton<Microsoft.Extensions.Logging.ILogger>(NullLogger.Instance);

            using var provider = services.BuildServiceProvider();

            var runExtractor = ExtractorModule.ResolveRunExtractor(provider);

            await runExtractor(cancellationToken);
        }

        public static Gen<Fixture> Generate() =>
            from resourceDtos in ResourceGenerator.GenerateResourceDtos()
            from supportedResources in Generator.SubSetOf([.. resourceDtos.Select(kvp => kvp.Key.Resource)])
            from resourcesToExtract in Generator.SubSetOf([.. from key in resourceDtos.Keys
                                                              where supportedResources.Contains(key.Resource)
                                                              select key ])
            select new Fixture
            {
                ApimResourceDtos = resourceDtos,
                SupportedApimResources = supportedResources,
                ResourcesToExtract = resourcesToExtract
            };
    }
}

file static class ResourceGenerator
{
    private static Lazy<ResourceGraph> LazyFullGraph { get; } = new(() =>
    {
        var builder = Host.CreateEmptyApplicationBuilder(default);
        ResourceGraphModule.ConfigureResourceGraph(builder);
        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<ResourceGraph>();
    });

    public static Gen<ImmutableDictionary<ResourceKey, Option<JsonObject>>> GenerateResourceDtos()
    {
        var graph = LazyFullGraph.Value;

        var rootResources = graph.ListTraversalRootResources();

        return from resources in Generator.Traverse(rootResources,
                                                    resource => GenerateResources(resource, ParentChain.Empty, graph))
               select resources.SelectMany(x => x)
                               .ToImmutableHashSet()
                               .ToImmutableDictionary(key => key,
                                                      key => key.Resource is IResourceWithDto
                                                                ? new JsonObject
                                                                {
                                                                    ["id"] = key.ToString()
                                                                }
                                                                : Option<JsonObject>.None());
    }

    private static Gen<ImmutableHashSet<ResourceKey>> GenerateResources(IResource resource, ParentChain ancestors, ResourceGraph graph) =>
        from resources in GenerateResources(resource, ancestors)
        from descendants in Generator.Traverse(resources,
                                               resourceKey => GenerateDescendants(resourceKey, graph))
                                     .Select(descendants => descendants.SelectMany(x => x).ToImmutableHashSet())
        select resources.Concat(descendants).ToImmutableHashSet();

    private static Gen<ImmutableHashSet<ResourceKey>> GenerateResources(IResource resource, ParentChain ancestors) =>
        from names in GenerateResourceNames(resource)
        let resources = names.Select(name => new ResourceKey
        {
            Name = name,
            Parents = ancestors,
            Resource = resource
        })
        select resources.ToImmutableHashSet();

    private static Gen<ImmutableHashSet<ResourceKey>> GenerateDescendants(ResourceKey resourceKey, ResourceGraph graph)
    {
        var (name, ancestors, resource) = (resourceKey.Name, resourceKey.Parents, resourceKey.Resource);
        var successorAncestors = ParentChain.From([.. ancestors, (resource, name)]);
        var successors = graph.ListTraversalSuccessors(resource);

        return from descendants in Generator.Traverse(successors,
                                                      successorResource => GenerateResources(successorResource, successorAncestors, graph))
               select descendants.SelectMany(x => x).ToImmutableHashSet();
    }

    private static Gen<ImmutableHashSet<ResourceName>> GenerateResourceNames(IResource resource) =>
        resource switch
        {
            ApiResource => GenerateApiNames(),
            WorkspaceApiResource => GenerateApiNames(),
            _ => Generator.ResourceName.HashSetOf(0, 10)
        };

    // We want to generate a mix of current and revisioned API names
    private static Gen<ImmutableHashSet<ResourceName>> GenerateApiNames() =>
        from currentNames in Generator.ResourceName.HashSetOf(0, 10)
        from currentNamesWithRevisions in Generator.SubSetOf(currentNames)
        from revisionedNames in Generator.Traverse(currentNamesWithRevisions,
                                                   name => from revision in Gen.Int[1, 100]
                                                           select ApiRevisionModule.Combine(name, revision))
        select currentNames.Concat(revisionedNames).ToImmutableHashSet();

    public static Gen<ImmutableHashSet<IResource>> Resources { get; } =
        Gen.OneOf(Generator.SubSetOf(LazyFullGraph.Value.TopologicallySortedResources),
                  Gen.Const(LazyFullGraph.Value.TopologicallySortedResources.ToImmutableHashSet()));

    public static Gen<ResourceKey> GenerateResourceKey(IResource resource)
    {
        return from name in Generator.ResourceName
               from parents in GenerateParentChain(resource)
               select new ResourceKey
               {
                   Name = name,
                   Parents = parents,
                   Resource = resource
               };
    }

    public static Gen<ParentChain> GenerateParentChain(IResource resource)
    {
        var parents = resource.GetTraversalPredecessorHierarchy();

        return from parentNames in Generator.Traverse(parents,
                                                      resource => from name in Generator.ResourceName
                                                                  select (resource, name))
               select ParentChain.From(parentNames);
    }
}