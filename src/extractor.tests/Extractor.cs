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
            var unsupportedResources = fixture.Resources.Where(resource => fixture.SupportedApimResources.Contains(resource) is false);
            var extractedResources = fixture.WrittenResourceDtos.Keys.Select(key => key.Resource);
            unsupportedResources.Should().NotIntersectWith(extractedResources);
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
                       .Should().BeEquivalentTo(kvp.Value);
            });
        });
    }

    [Fact]
    public async Task Children_of_unsupported_parents_are_not_extracted()
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
                var parents = kvp.Key.Resource.GetTraversalPredecessorHierarchy();
                parents.Should().BeSubsetOf(fixture.SupportedApimResources);
            });
        });
    }

    private sealed record Fixture
    {
        private ImmutableDictionary<ResourceKey, JsonObject> writtenResourceDtos = [];
        public required ImmutableHashSet<IResource> Resources { get; init; }
        public required ImmutableHashSet<IResource> SupportedApimResources { get; init; }
        public required ImmutableDictionary<(IResource, ParentChain), ImmutableHashSet<ResourceName>> ApimResourceNames { get; init; }
        public required ImmutableDictionary<ResourceKey, JsonObject> ApimResourceDtos { get; init; }
        public required ImmutableHashSet<ResourceKey> ResourcesToExtract { get; init; }
        public ImmutableDictionary<ResourceKey, JsonObject> WrittenResourceDtos => writtenResourceDtos;

        public async ValueTask Run(CancellationToken cancellationToken)
        {
            var services = new ServiceCollection();

            services.AddSingleton<ResourceGraph>(ResourceGraph.From(Resources, cancellationToken))
                    .AddSingleton<IsResourceSupportedInApim>(async (resource, cancellationToken) =>
                    {
                        await ValueTask.CompletedTask;
                        return SupportedApimResources.Contains(resource);
                    })
                    .AddSingleton<ListResourceNamesFromApim>((resource, parents, cancellationToken) =>
                    {
                        return ApimResourceNames.Find((resource, parents))
                                                .IfNone(() => [])
                                                .ToAsyncEnumerable();
                    })
                    .AddSingleton<ListResourceDtosFromApim>((resource, parents, cancellationToken) =>
                    {
                        if (resource is not IResourceWithDto resourceWithDto)
                        {
                            return AsyncEnumerable.Empty<(ResourceName, JsonObject)>();
                        }

                        return ApimResourceNames.Find((resourceWithDto, parents))
                                                .IfNone(() => [])
                                                .ToAsyncEnumerable()
                                                .Choose(resourceName =>
                                                {
                                                    var resourceKey = new ResourceKey
                                                    {
                                                        Name = resourceName,
                                                        Parents = parents,
                                                        Resource = resourceWithDto
                                                    };

                                                    return ApimResourceDtos.Find(resourceKey)
                                                                           .Map(dto => (resourceName, dto));
                                                });
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
            from resources in ResourceGenerator.Resources
            from supportedApimResources in Generator.SubSetOf(resources)
            from apimResourceNames in from keys in Generator.Traverse(resources,
                                                                      resource => from parentChain in ResourceGenerator.GenerateParentChain(resource)
                                                                                  from names in Generator.ResourceName.HashSetOf()
                                                                                  select KeyValuePair.Create((resource, parentChain), names))
                                      select keys.ToImmutableDictionary()
            let resourceKeys = apimResourceNames.SelectMany(kvp => from name in kvp.Value
                                                                   select new ResourceKey
                                                                   {
                                                                       Name = name,
                                                                       Parents = kvp.Key.parentChain,
                                                                       Resource = kvp.Key.resource
                                                                   })
                                                .ToImmutableHashSet()
            let apimResourceDtos = resourceKeys.ToImmutableDictionary(key => key,
                                                                      key => new JsonObject
                                                                      {
                                                                          ["id"] = key.ToString()
                                                                      })
            from resourcesToExtract in Generator.SubSetOf(resourceKeys)
            select new Fixture
            {
                Resources = resources,
                SupportedApimResources = supportedApimResources,
                ApimResourceNames = apimResourceNames,
                ApimResourceDtos = apimResourceDtos,
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

    public static Gen<ImmutableHashSet<IResource>> Resources { get; } =
        Generator.SubSetOf(LazyFullGraph.Value.TopologicallySortedResources);

    public static Gen<ParentChain> GenerateParentChain(IResource resource)
    {
        var parents = resource.GetTraversalPredecessorHierarchy();

        return from parentNames in Generator.Traverse(parents,
                                                      resource => from name in Generator.ResourceName
                                                                  select (resource, name))
               select ParentChain.From(parentNames);
    }

    public static Gen<ResourceKey> GenerateResourceKey(IResource resource) =>
        from name in Generator.ResourceName
        from parents in GenerateParentChain(resource)
        select new ResourceKey
        {
            Name = name,
            Parents = parents,
            Resource = resource
        };
}