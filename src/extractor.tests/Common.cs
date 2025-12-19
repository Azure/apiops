using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;

namespace extractor.tests;

internal static class ResourceGenerator
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

        return from resources in Generator.SubSetOf(rootResources)
               from resourceKeys in Generator.Traverse(resources,
                                                       resource => GenerateResources(resource, ParentChain.Empty, graph))
                                             .Select(resources => resources.SelectMany(x => x).ToImmutableHashSet())
               select resourceKeys.ToImmutableDictionary(key => key,
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
            _ => Generator.ResourceName.HashSetOf(0, 4)
        };

    // We want to generate a mix of current and revisioned API names
    private static Gen<ImmutableHashSet<ResourceName>> GenerateApiNames() =>
        from currentNames in Generator.ResourceName.HashSetOf(0, 4)
        from currentNamesWithRevisions in Generator.SubSetOf(currentNames)
        from revisionedNames in Generator.Traverse(currentNamesWithRevisions,
                                                   name => from revision in Gen.Int[1, 100]
                                                           select ApiRevisionModule.Combine(name, revision))
        select currentNames.Concat(revisionedNames).ToImmutableHashSet();

    public static Gen<ResourceKey> GenerateResourceKey() =>
        from resource in Gen.OneOfConst([.. LazyFullGraph.Value.TopologicallySortedResources])
        from resourceKey in GenerateResourceKey(resource)
        select resourceKey;

    public static Gen<ResourceKey> GenerateResourceKey(Func<IResource, bool> resourcePredicate) =>
        from resource in Gen.OneOfConst([.. LazyFullGraph.Value.TopologicallySortedResources
                                                         .Where(resourcePredicate)])
        from resourceKey in GenerateResourceKey(resource)
        select resourceKey;

    public static Gen<ResourceKey> GenerateResourceKey(IResource resource) =>
        from name in Generator.ResourceName
        from parents in Generator.Traverse(resource.GetTraversalPredecessorHierarchy(),
                                           parentResource => from parentName in Generator.ResourceName
                                                             select (parentResource, parentName))
        let parentChain = ParentChain.From(parents)
        select new ResourceKey
        {
            Name = name,
            Parents = parentChain,
            Resource = resource
        };
}

internal static class ServiceCollectionModule
{
    public static IServiceCollection AddTestActivitySource(this IServiceCollection services) =>
        services.AddSingleton<ActivitySource>(_ => new ActivitySource("extractor.tests"));

    public static IServiceCollection AddNullLogger(this IServiceCollection services) =>
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(NullLogger.Instance);
}