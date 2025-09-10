using common;
using CsCheck;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal interface ITestModel
{
    public ResourceName Name { get; }
    public IResource AssociatedResource { get; }
}

internal interface ITestModel<T> : ITestModel where T : ITestModel<T>
{
    public static abstract Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline);

    public new static abstract IResource AssociatedResource { get; }
    IResource ITestModel.AssociatedResource => T.AssociatedResource;
}

internal interface IDtoTestModel : ITestModel
{
    public JsonObject SerializeDto(ModelNodeSet predecessors);
    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson);

    public new IResourceWithDto AssociatedResource { get; }
    IResource ITestModel.AssociatedResource => AssociatedResource;
}

internal interface IDtoTestModel<T> : IDtoTestModel, ITestModel<T> where T : IDtoTestModel<T>
{
    public new static abstract IResourceWithDto AssociatedResource { get; }
    IResourceWithDto IDtoTestModel.AssociatedResource => T.AssociatedResource;
    static IResource ITestModel<T>.AssociatedResource => T.AssociatedResource;
    IResource ITestModel.AssociatedResource => T.AssociatedResource;
}

internal interface ICompositeResourceTestModel : IDtoTestModel
{
    public ResourceName PrimaryResourceName { get; }
    public ResourceName SecondaryResourceName { get; }
    ResourceName ITestModel.Name => ResourceName.From($"{PrimaryResourceName}-{SecondaryResourceName}")
                                                .IfErrorThrow();

    public new ICompositeResource AssociatedResource { get; }
    IResourceWithDto IDtoTestModel.AssociatedResource => AssociatedResource;

    JsonObject IDtoTestModel.SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(DirectCompositeDto.Instance, AssociatedResource.SerializerOptions)
                        .IfErrorThrow();

    bool IDtoTestModel.MatchesDto(JsonObject json, Option<JsonObject> overrideJson) => true;
}

internal interface ICompositeResourceTestModel<T> : ICompositeResourceTestModel, IDtoTestModel<T> where T : ICompositeResourceTestModel<T>
{
    public new static abstract ICompositeResource AssociatedResource { get; }
    ICompositeResource ICompositeResourceTestModel.AssociatedResource => T.AssociatedResource;
    static IResourceWithDto IDtoTestModel<T>.AssociatedResource => T.AssociatedResource;
    IResourceWithDto IDtoTestModel.AssociatedResource => T.AssociatedResource;
}

internal interface ILinkResourceTestModel : ICompositeResourceTestModel, IDtoTestModel
{
    public new ILinkResource AssociatedResource { get; }
    IResource ITestModel.AssociatedResource => AssociatedResource;

    JsonObject IDtoTestModel.SerializeDto(ModelNodeSet predecessors)
    {
        var secondaryNodes = predecessors.Where(node => node.Model.AssociatedResource == AssociatedResource.Secondary);
        var secondaryNode = secondaryNodes.ToImmutableArray() switch
        {
            [] => throw new InvalidOperationException($"No secondary resource found for {AssociatedResource.SingularName} {Name}."),
            [var predecessor] => predecessor,
            _ => throw new InvalidOperationException($"Multiple secondary resources found for {AssociatedResource.SingularName} {Name}.")
        };

        return JsonObjectModule.From(new LinkResourceDto()
        {
            Name = Name.ToString(),
            Properties = new JsonObject
            {
                [AssociatedResource.DtoPropertyNameForLinkedResource] = secondaryNode.ToResourceId()
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();
    }
}

internal interface ILinkResourceTestModel<T> : ILinkResourceTestModel, ICompositeResourceTestModel<T> where T : ILinkResourceTestModel<T>
{
    public new static abstract ILinkResource AssociatedResource { get; }
    ILinkResource ILinkResourceTestModel.AssociatedResource => T.AssociatedResource;
    static ICompositeResource ICompositeResourceTestModel<T>.AssociatedResource => T.AssociatedResource;
    IResource ITestModel.AssociatedResource => T.AssociatedResource;
}

internal interface IResourceWithReferenceTestModel<T> : IDtoTestModel<T> where T : IResourceWithReferenceTestModel<T>
{
    public new static abstract IResourceWithReference AssociatedResource { get; }
    static IResourceWithDto IDtoTestModel<T>.AssociatedResource => T.AssociatedResource;
}

internal interface IPolicyResourceTestModel<T> : IDtoTestModel<T> where T : IPolicyResourceTestModel<T>
{
    public string Content { get; }

    public new static abstract IPolicyResource AssociatedResource { get; }
    static IResourceWithDto IDtoTestModel<T>.AssociatedResource => T.AssociatedResource;

    ResourceName ITestModel.Name => ResourceName.From("policy")
                                                .IfErrorThrow();

    JsonObject IDtoTestModel.SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new PolicyDto()
        {
            Properties = new PolicyDto.PolicyContract
            {
                Format = "rawxml",
                Value = Content
            }
        }, T.AssociatedResource.SerializerOptions).IfErrorThrow();

    bool IDtoTestModel.MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<ApiDto>(json, T.AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<ApiDto>(json, T.AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Content = overrideDto?.Properties?.Value ?? Content
        };

        var right = new
        {
            Content = jsonDto?.Properties?.Value
        };

        return left.Content.FuzzyEqualsPolicy(right.Content);
    }
}

internal sealed record ModelNode
{
    public ITestModel Model { get; }
    public ModelNodeSet Predecessors { get; }

    private ModelNode(ITestModel model, IEnumerable<ModelNode> predecessors)
    {
        Model = model;
        Predecessors = ModelNodeSet.From(predecessors);
    }

    public bool Equals(ModelNode? other) =>
        other is not null
        && Model.GetType() == other.Model.GetType()
        && Model.Name == other.Model.Name
        && Predecessors == other.Predecessors;

    public override int GetHashCode() =>
        Model.GetType().GetHashCode()
        ^ Model.Name.GetHashCode()
        ^ Predecessors.GetHashCode();

    public static ModelNode From(ITestModel model, IEnumerable<ModelNode> predecessors)
    {
        var node = new ModelNode(model, predecessors);

        // Ensure there are no cycles in the predecessor chain
        var visitedSet = new HashSet<ITestModel>();
        checkForCycles(node);

        return node;

        void checkForCycles(ModelNode node)
        {
            if (visitedSet.Add(node.Model) is false)
            {
                throw new InvalidOperationException($"Cycle detected in predecessors for model {node.Model.Name}");
            }

            foreach (var predecessor in node.Predecessors)
            {
                checkForCycles(predecessor);
            }

            visitedSet.Remove(node.Model);
        }
    }
}

internal static class ModelNodeModule
{
    public static ResourceAncestors GetResourceAncestors(this ModelNode node)
    {
        var ancestors = ResourceAncestors.Empty;
        populateAncestors(node);
        return ancestors;

        void populateAncestors(ModelNode node) =>
            getAncestor(node).Iter(ancestor =>
            {
                ancestors = ancestors.Prepend(ancestor.Model.AssociatedResource, ancestor.Model.Name);
                populateAncestors(ancestor);
            });

        Option<ModelNode> getAncestor(ModelNode node) =>
            from ancestorResource in node.Model.AssociatedResource.GetTraversalPredecessor()
            from ancestor in node.Predecessors
                                 .Where(predecessor => predecessor.Model.AssociatedResource == ancestorResource)
                                 .ToImmutableArray() switch
            {
                [var predecessor] => Option.Some(predecessor),
                [] => Option.None,
                _ => throw new InvalidOperationException($"Multiple predecessors found for resource {ancestorResource.GetType().Name}.")
            }
            select ancestor;
    }

    public static string ToResourceId(this ModelNode node) =>
        node.GetResourceAncestors()
            .Append(node.Model.AssociatedResource, node.Model.Name)
            .ToResourceId();

    public static JsonObject Serialize(this ModelNode node) =>
        new()
        {
            ["model"] = node.Model is IDtoTestModel dtoModel
                        ? dtoModel.SerializeDto(node.Predecessors)
                                  .SetProperty("name", node.Model.Name.ToString())
                        : "<non-dto>",
            ["predecessors"] = node.Predecessors.Select(predecessor => predecessor.Serialize())
                                           .ToJsonArray()
        };
}

internal sealed record ModelNodeSet : IEnumerable<ModelNode>
{
    private readonly ImmutableHashSet<ModelNode> nodes;

    private ModelNodeSet(IEnumerable<ModelNode> nodes) =>
        this.nodes = [.. nodes];

    public ModelNodeSet Add(ModelNode node) =>
        Add([node]);

    public ModelNodeSet Add(IEnumerable<ModelNode> nodes) =>
        From([.. this.nodes, .. nodes]);

    public int Count => nodes.Count;

    public JsonArray Serialize() =>
        nodes.Select(node => node.Serialize())
             .ToJsonArray();

    public bool Equals(ModelNodeSet? other) =>
        other is not null
        && nodes.SetEquals(other.nodes);

    public override int GetHashCode() =>
        HashSet<ModelNode>.CreateSetComparer().GetHashCode([.. nodes]);

    public static ModelNodeSet Empty { get; } = new([]);

    public static ModelNodeSet From(IEnumerable<ModelNode> nodes) => new(nodes);

    // IEnumerable implementation
    public IEnumerator<ModelNode> GetEnumerator() => nodes.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class ModelNodeSetModule
{
    public static Option<ModelNode> Pick<T>(this IEnumerable<ModelNode> nodes) where T : IResource =>
        nodes.Pick(node => node.Model.AssociatedResource is T
                           ? Option.Some(node)
                           : Option.None);

    public static Option<ResourceName> PickName<T>(this IEnumerable<ModelNode> nodes) where T : IResource =>
        from node in Pick<T>(nodes)
        select node.Model.Name;

    public static ResourceName PickNameOrThrow<T>(this IEnumerable<ModelNode> nodes) where T : IResource =>
        nodes.PickName<T>()
             .IfNone(() => throw new InvalidOperationException($"No node of type {typeof(T).Name} found in the set."));

    public static Option<ModelNode> Find(this IEnumerable<ModelNode> nodes, ResourceName name, IEnumerable<ModelNode> predecessors)
    {
        var parameterSet = ModelNodeSet.From(predecessors);

        return nodes.Find(name)
                    .Where(node => node.Predecessors == parameterSet)
                    .Head();
    }

    public static Option<ModelNode> Find(this IEnumerable<ModelNode> nodes, ResourceName name, ResourceAncestors ancestors) =>
        nodes.Find(name)
             .Where(node => node.GetResourceAncestors() == ancestors)
             .Head();

    private static IEnumerable<ModelNode> Find(this IEnumerable<ModelNode> nodes, ResourceName name) =>
        nodes.Where(node => node.Model.Name == name);
}

internal sealed record ResourceModels : IImmutableDictionary<IResource, ModelNodeSet>
{
    private readonly ImmutableDictionary<IResource, ModelNodeSet> value;

    private ResourceModels(IEnumerable<KeyValuePair<IResource, ModelNodeSet>> value) => this.value = value.ToImmutableDictionary();

    public static ResourceModels Empty { get; } = new([]);

    public static ResourceModels From(IEnumerable<ModelNode> nodes) =>
        new([.. from node in nodes
                group node by node.Model.AssociatedResource into @group
                let set = ModelNodeSet.From(@group)
                select KeyValuePair.Create(@group.Key, set)]);

    public ResourceModels Add(IEnumerable<ModelNode> nodes)
    {
        var updatedDictionary = value.ToDictionary();

        nodes.GroupBy(node => node.Model.AssociatedResource)
             .Iter(group =>
             {
                 var resource = group.Key;

                 var set = updatedDictionary.Find(resource)
                                            .Map(existing => existing.Add(group))
                                            .IfNone(() => ModelNodeSet.From(group));

                 updatedDictionary[resource] = set;
             });

        return new(updatedDictionary);
    }

    public Option<ModelNodeSet> Find(IResource resource) =>
        value.Find(resource);

    // Collection interface implementations
    public IEnumerator<KeyValuePair<IResource, ModelNodeSet>> GetEnumerator() => value.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int Count => value.Count;
    public IEnumerable<IResource> Keys => value.Keys;
    public IEnumerable<ModelNodeSet> Values => value.Values;
    public ModelNodeSet this[IResource key] => value[key];
    public bool TryGetValue(IResource key, [MaybeNullWhen(false)] out ModelNodeSet value) => this.value.TryGetValue(key, out value);
    public bool ContainsKey(IResource key) => value.ContainsKey(key);
    public bool TryGetKey(IResource equalKey, out IResource actualKey) => value.TryGetKey(equalKey, out actualKey);
    public bool Contains(KeyValuePair<IResource, ModelNodeSet> pair) => value.Contains(pair);
    public IImmutableDictionary<IResource, ModelNodeSet> Add(IResource key, ModelNodeSet value) => this.value.Add(key, value);
    public IImmutableDictionary<IResource, ModelNodeSet> AddRange(IEnumerable<KeyValuePair<IResource, ModelNodeSet>> pairs) => this.value.AddRange(pairs);
    public IImmutableDictionary<IResource, ModelNodeSet> Clear() => this.value.Clear();
    public IImmutableDictionary<IResource, ModelNodeSet> Remove(IResource key) => this.value.Remove(key);
    public IImmutableDictionary<IResource, ModelNodeSet> RemoveRange(IEnumerable<IResource> keys) => this.value.RemoveRange(keys);
    public IImmutableDictionary<IResource, ModelNodeSet> SetItem(IResource key, ModelNodeSet value) => this.value.SetItem(key, value);
    public IImmutableDictionary<IResource, ModelNodeSet> SetItems(IEnumerable<KeyValuePair<IResource, ModelNodeSet>> items) => this.value.SetItems(items);
}

internal static class ResourceModelsModule
{
    public static Option<ModelNode> Find(this ResourceModels models, IResource resource, ResourceName name, IEnumerable<ModelNode> predecessors) =>
        from set in models.Find(resource)
        from node in set.Find(name, predecessors)
        select node;

    public static Option<ModelNode> Find(this ResourceModels models, IResource resource, ResourceName name, ResourceAncestors ancestors) =>
        from set in models.Find(resource)
        from node in set.Find(name, ancestors)
        select node;

    public static ImmutableArray<(T Model, ModelNodeSet Predecessors)> Choose<T>(this ResourceModels models) where T : ITestModel<T> =>
        [.. models.Find(T.AssociatedResource)
                  .IfNone(() => ModelNodeSet.Empty)
                  .Choose(node => node.Model is T model
                                    ? Option.Some((model, node.Predecessors))
                                    : Option.None)];

    public static JsonArray Serialize(this ResourceModels models) =>
        models.Select(kvp => new JsonObject
        {
            ["resource"] = kvp.Key.GetType().Name,
            ["nodes"] = kvp.Value.Serialize()
        }).ToJsonArray();
}