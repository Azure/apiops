using Bogus;
using Bogus.DataSets;
using common;
using CsCheck;
using DotNext;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json.Nodes;

namespace integration.tests;

internal static class Generator
{
    public static Gen<Randomizer> Randomizer { get; } =
        from seed in Gen.Int.Positive
        select new Randomizer(seed);

    public static Gen<Lorem> Lorem { get; } =
        from randomizer in Randomizer
        select new Lorem { Random = randomizer };

    private static Gen<Internet> Internet { get; } =
        from randomizer in Randomizer
        select new Internet { Random = randomizer };

    public static Gen<Uri> Uri { get; } =
        from internet in Internet
        select new Uri(internet.Url());

    public static Gen<Address> Address { get; } =
        from randomizer in Randomizer
        select new Address { Random = randomizer };

    public static Gen<string> AlphanumericWord { get; } =
        from randomizer in Randomizer
        let word = randomizer.Word()
        where word.All(char.IsLetterOrDigit)
        select word;

    public static Gen<ResourceName> ResourceName { get; } =
        from words in AlphanumericWord.Array[1, 5]
        where words.Sum(word => word.Length) <= 16
        let name = string.Join("-", words).ToLowerInvariant()
        select common.ResourceName.From(name)
                                  .IfErrorThrow();

    public static Gen<ServiceDirectory> ServiceDirectory { get; } =
        from characters in Gen.OneOf([Gen.Char['a', 'z'], Gen.Char['0', '9']]).Array[8]
        let directoryName = $"apiops-{new string(characters)}"
        let path = Path.Combine(Path.GetTempPath(), directoryName)
        select common.ServiceDirectory.FromPath(path);

    public static Gen<string> IpFilterPolicySnippet { get; } =
        from last3 in Gen.Int[0, 255].Array[3]
        let ips = last3.Prepend(10)
        let address = string.Join('.', ips)
        select $"""
                <ip-filter action="allow">
                    <address>{address}</address>
                </ip-filter>
                """;

    public static Gen<string> InboundPolicySnippet { get; } =
        from ipFilterSnippet in IpFilterPolicySnippet
        select $"""
                <inbound>
                    {ipFilterSnippet}
                </inbound>
                """;

    private static Gen<(string Name, string Value)> Header { get; } =
        Gen.OneOf(from contentType in Gen.OneOfConst("application/json", "application/xml", "text/plain")
                  select ("Content-Type", contentType),
                  from customHeaderChars in Gen.Char.AlphaNumeric.Array[1, 20]
                  let customHeader = new string(customHeaderChars)
                  select ("X-Custom-Header", customHeader));

    public static Gen<string> SetHeaderPolicySnippet { get; } =
        from x in Header
        select $"""
                <set-header name="{x.Name}" exists-action="append">
                    <value>{x.Value}</value>
                </set-header>
                """;

    public static Gen<string> OutboundPolicySnippet { get; } =
        from setHeaderSnippet in SetHeaderPolicySnippet
        select $"""
                <outbound>
                    {setHeaderSnippet}
                </outbound>
                """;

    public static Gen<ResourceModels> GenerateResourceModels(ResourceGraph graph) =>
        GenerateResourceModels(baseline: ResourceModels.Empty, graph);

    private static Gen<ResourceModels> GenerateResourceModels(ResourceModels baseline, ResourceGraph graph)
    {
        var predecessorComparer = getPredecessorComparer();

        var generator =
            graph.TopologicallySortedResources
                 .Aggregate(Gen.Const(ResourceModels.Empty),
                            (generator, resource) => from accumulated in generator
                                                     let accumulatedNodes = accumulated.SelectMany(kvp => kvp.Value).ToImmutableHashSet()
                                                     // Use baseline + accumulated to generate brand-new/existing/unchanged
                                                     let effectiveBaseline = ResourceModels.From([// Potential predecessors
                                                                                                  .. accumulatedNodes,
                                                                                                  // Resource nodes in baseline
                                                                                                  .. from node in baseline.Find(resource).IfNone(() => ModelNodeSet.Empty)
                                                                                                     // Skip nodes whose predecessors are not all included in the accumulated set
                                                                                                     where node.Predecessors.All(predecessor => accumulatedNodes.Contains(predecessor, predecessorComparer))
                                                                                                     select node])
                                                     let getNodesGenerator = nodeGenerators[resource]
                                                     from nodes in getNodesGenerator(effectiveBaseline)
                                                     select accumulated.Add([.. nodes]));

        return generator.Select(models =>
        {
            var nodes = ModelNodeSet.From(models.SelectMany(kvp => kvp.Value));
            nodes = removeInvalidVersionSets(nodes);
            return ResourceModels.From(nodes);
        });

        static EqualityComparer<ModelNode> getPredecessorComparer() =>
            EqualityComparer<ModelNode>.Create((x, y) => x is not null
                                                         && y is not null
                                                         && x.Model == y.Model && x.Predecessors
                                                                                   .ToImmutableHashSet(getPredecessorComparer())
                                                                                   .SetEquals(y.Predecessors),
                                               x => HashCode.Combine(x.Model, x.Predecessors.Count));

        ModelNodeSet removeInvalidVersionSets(ModelNodeSet nodes) =>
            ModelNodeSet.From(from node in nodes
                              where node.Model is not VersionSetModel
                                    // All version sets must be associated with an API
                                    || nodes.Any(successor => successor.Model is ApiModel
                                                              && successor.Predecessors.Contains(node, predecessorComparer))
                              select node);

    }

    public static Gen<ResourceModels> GenerateSubSetOf(ResourceModels models)
    {
        var nodes = models.SelectMany(kvp => kvp.Value);

        return from nodeSubset in Generator.SubSetOf([.. nodes])
               let nodeSubsetWithAllApiRevisions = includeAllApiRevisions(nodeSubset)
               let subsetWithDependencies = getNodeDependencies(nodeSubsetWithAllApiRevisions)
               select ResourceModels.From(subsetWithDependencies);

        // All API revisions must be included if any revision is included
        ImmutableArray<ModelNode> includeAllApiRevisions(IEnumerable<ModelNode> subset)
        {
            var subsetList = subset.ToList();

            var subsetApiNames = subset.Choose(node => node.Model is ApiModel apiModel
                                                        ? Option.Some(apiModel.Name)
                                                        : Option.None)
                                       .ToImmutableHashSet();

            var subsetRootApiNames = subsetApiNames.Select(ApiRevisionModule.GetRootName)
                                                   .ToImmutableHashSet();

            models.Find(ApiResource.Instance)
                  .Iter(set => set.Iter(node =>
                  {
                      var name = node.Model.Name;
                      var rootName = ApiRevisionModule.GetRootName(name);

                      if (subsetRootApiNames.Contains(rootName)
                          && subsetApiNames.Contains(name) is false)
                      {
                          subsetList.Add(node);
                      }
                  }));

            return [.. subsetList];
        }

        // Ensure all dependencies are included
        static IEnumerable<ModelNode> getNodeDependencies(IEnumerable<ModelNode> nodes)
        {
            var visited = new HashSet<ModelNode>();

            var stack = new Stack<ModelNode>(nodes);

            while (stack.TryPop(out var node))
            {
                if (visited.Add(node))
                {
                    node.Predecessors.Iter(stack.Push);
                }
            }

            return visited;
        }
    }

    public static Gen<JsonObject> GeneratePublisherOverrides(ResourceModels models, ResourceGraph graph)
    {
        return from updatedModels in GenerateUpdatedResourceModels(models, graph)
               let updatedNodes = updatedModels.SelectMany(kvp => kvp.Key switch
               {
                   ApiResource => normalizeApis(kvp.Value),
                   _ => from node in kvp.Value
                        where models.Find(node.Model.AssociatedResource, node.Model.Name, node.Predecessors).IsSome
                        select node
               })
               select resourceModelsToJson(ResourceModels.From(updatedNodes));

        IEnumerable<ModelNode> normalizeApis(IEnumerable<ModelNode> updated)
        {
            var dictionary = updated.Choose(node => node.Model is ApiModel apiModel
                                                    && ApiRevisionModule.IsRootName(apiModel.Name)
                                                    ? Option.Some(new { apiModel.Name, apiModel.Description })
                                                    : Option.None)
                                    .GroupBy(x => x.Name)
                                    .ToImmutableDictionary(group => group.Key, group => group.First());

            return models.Choose<ApiModel>()
                         .Choose(x =>
                         {
                             var (name, predecessors) = (x.Model.Name, x.Predecessors);
                             var rootName = ApiRevisionModule.GetRootName(name);

                             return from updated in dictionary.Find(rootName)
                                    let model = x.Model with { Description = updated.Description }
                                    select ModelNode.From(model, predecessors);
                         });
        }

        JsonObject resourceModelsToJson(ResourceModels models) =>
                graph.GetTraversalRootResources()
                     .Aggregate(new JsonObject(),
                                (json, resource) =>
                                {
                                    var nodes = getModelNodeSet(resource, models);

                                    return json.SetProperty(resource.PluralName,
                                                            modelNodeSetToJson(nodes, models),
                                                            mutateOriginal: true);
                                });

        static ModelNodeSet getModelNodeSet(IResource resource, ResourceModels models) =>
            models.Find(resource)
                  .IfNone(() => ModelNodeSet.Empty);

        JsonArray modelNodeSetToJson(ModelNodeSet nodes, ResourceModels models) =>
            nodes.Select(node => modelNodeToJson(node, models))
                 .ToJsonArray();

        JsonObject modelNodeToJson(ModelNode node, ResourceModels models)
        {
            var model = node.Model;
            var name = model.Name;
            var json = new JsonObject { ["name"] = name.ToString() };

            if (model is IDtoTestModel dtoTestModel)
            {
                json = json.MergeWith(dtoTestModel.SerializeDto(node.Predecessors), mutateOriginal: true);
            }

            var successors = from successorResource in graph.GetTraversalSuccessors(model.AssociatedResource)
                             let successorSet =
                                from successor in getModelNodeSet(successorResource, models)
                                where successor.Predecessors.Find(name, node.Predecessors).IsSome
                                select successor
                             select (Resource: successorResource, Set: successorSet);

            return successors.ToArray() switch
            {
                [] => json,
                var array => array.Select(successor => new JsonObject
                {
                    [successor.Resource.PluralName] = successor.Set
                                                               .Select(successor => modelNodeToJson(successor, models))
                                                               .ToJsonArray()
                }).Aggregate(json, (current, successorJson) => current.MergeWith(successorJson))
            };
        }
    }

    public static Gen<ResourceModels> GenerateUpdatedResourceModels(ResourceModels originalModels, ResourceGraph graph) =>
        GenerateResourceModels(originalModels, graph);

    public static Gen<Option<T>> OptionOf<T>(this Gen<T> gen) =>
        Gen.Frequency((4, from t in gen
                          select Option.Some(t)),
                      (1, Gen.Const(Option<T>.None())));

    public static Gen<T> OrConst<T>(this Gen<T> gen, T t) =>
        Gen.OneOf([gen, Gen.Const(t)]);

    /// <remarks>
    /// If <paramref name="option"/> is Some, the generator should return a Some value from <paramref name="gen"/>.
    /// This addresses issues with publisher overrides.
    /// </remarks>
    public static Gen<Option<T>> OrConst<T>(this Gen<Option<T>> gen, Option<T> option) =>
        Gen.OneOf([option.Map(_ => gen.Where(option => option.IsSome))
                         .IfNone(() => gen),
                   Gen.Const(option)]);

    public static Gen<ImmutableHashSet<T>> SubSetOf<T>(ICollection<T> collection) =>
        SubSetOf(collection, minimumLength: 0, maximumLength: collection.Count);

    public static Gen<ImmutableHashSet<T>> SubSetOf<T>(ICollection<T> collection, int minimumLength, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, minimumLength);

        return from length in Gen.Int[minimumLength, Math.Min(collection.Count, maximumLength)]
               from set in length switch
               {
                   0 => Gen.Const(ImmutableHashSet<T>.Empty),
                   _ => from list in Gen.Shuffle(constants: [.. collection], length)
                        select list.ToImmutableHashSet()
               }
               select set;
    }

    public static Gen<ImmutableHashSet<T>> HashSetOf<T>(this Gen<T> gen, IEqualityComparer<T>? comparer = default) =>
        gen.HashSetOf(minimumLength: 0, maximumLength: 10, comparer);

    public static Gen<ImmutableHashSet<T>> HashSetOf<T>(this Gen<T> gen, int length, IEqualityComparer<T>? comparer = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return gen.HashSetOf(minimumLength: length, maximumLength: length, comparer);
    }

    public static Gen<ImmutableHashSet<T>> HashSetOf<T>(this Gen<T> gen, int minimumLength, int maximumLength, IEqualityComparer<T>? comparer = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, minimumLength);

        return from items in gen.Array[minimumLength, maximumLength]
               let set = comparer is null
                   ? [.. items]
                   : items.ToImmutableHashSet(comparer)
               where set.Count >= minimumLength && set.Count <= maximumLength
               select set;
    }

    public static Gen<ImmutableArray<T2>> Traverse<T1, T2>(IEnumerable<T1> source, Func<T1, Gen<T2>> f) =>
        source.Aggregate(Gen.Const(ImmutableArray.Create<T2>()),
                         (arrayGen, t1) => from array in arrayGen
                                           from t2 in f(t1)
                                           select array.Add(t2));

    public static Gen<ImmutableArray<(T3, T2)>> Traverse<T1, T2, T3>(IEnumerable<(T1, T2)> source, Func<T1, Gen<T3>> f) =>
        Generator.Traverse(source,
                           item => from t3 in f(item.Item1)
                                   select (t3, item.Item2));

    private static readonly ImmutableDictionary<IResource, Func<ResourceModels, Gen<ModelNodeSet>>> nodeGenerators =
        GetNodeGenerators();

    private static ImmutableDictionary<IResource, Func<ResourceModels, Gen<ModelNodeSet>>> GetNodeGenerators()
    {
        var modelsParameter = Expression.Parameter(typeof(ResourceModels), "models");

        var modelTypes = from type in typeof(ITestModel<>).Assembly.GetTypes()
                         where type.IsClass
                         where type.IsAbstract is false
                         where type.GetInterfaces().Any(i => i.IsGenericType
                                                             && i.GetGenericTypeDefinition() == typeof(ITestModel<>))
                         select type;

        return modelTypes.ToImmutableDictionary(getResource, getNodeGenerator);

        static IResource getResource(Type type)
        {
            // Create () => T.AssociatedResource
            var propertyExpression = Expression.Property(null, type, nameof(ITestModel<>.AssociatedResource));
            var lambaExpression = Expression.Lambda<Func<IResource>>(propertyExpression);
            var func = lambaExpression.Compile();

            return func();
        }

        Func<ResourceModels, Gen<ModelNodeSet>> getNodeGenerator(Type type)
        {
            var call = Expression.Call(type, nameof(ITestModel<>.GenerateNodes), typeArguments: null, modelsParameter);

            return Expression.Lambda<Func<ResourceModels, Gen<ModelNodeSet>>>(call, modelsParameter)
                             .Compile();
        }
    }

    public static Gen<ModelNodeSet> GenerateNodes<T>(ResourceModels baseline,
                                                     Func<T, Gen<T>> getUpdate,
                                                     Gen<(T, ModelNodeSet Predecessors)> newGen)
        where T : ITestModel<T>
    {
        var existing = baseline.Choose<T>();

        var unchangedGenerator = Generator.SubSetOf([.. existing]);

        var updatedGenerator = from toUpdate in Generator.SubSetOf([.. existing])
                               from updated in Generator.Traverse(toUpdate, getUpdate)
                               select updated;

        var newSetGenerator = newGen.HashSetOf();

        return from unchanged in unchangedGenerator
               from updated in updatedGenerator
               from @new in newSetGenerator
               let nodes = from all in unchanged.Union(updated).Union(@new)
                           select ModelNode.From(all.Model, all.Predecessors)
               select ModelNodeSet.From(nodes);
    }

    public static Option<Gen<ModelNodeSet>> GeneratePredecessors<T>(ResourceModels baseline) where T : ITestModel<T>
    {
        var resource = T.AssociatedResource;
        var generatorOption = Option.Some(Gen.Const(ModelNodeSet.Empty));

        if (resource is IChildResource childResource)
        {
            var newGeneratorOption = generateParent(childResource);
            generatorOption = mergeNodeGenerator(generatorOption, newGeneratorOption);
        }

        if (resource is ICompositeResource compositeResource)
        {
            var newGeneratorOption = generateCompositePredecessors(compositeResource);
            generatorOption = mergeNodesGenerator(generatorOption, newGeneratorOption);
        }

        if (resource is IResourceWithReference resourceWithReference)
        {
            var mandatoryGeneratorOption = generateMandatoryReferences(resourceWithReference);
            generatorOption = mergeNodesGenerator(generatorOption, mandatoryGeneratorOption);

            var optionalGeneratorOption = generateOptionalReferences(resourceWithReference);
            generatorOption = mergeNodesGenerator(generatorOption, Option.Some(optionalGeneratorOption));
        }

        return generatorOption;

        Option<Gen<ModelNode>> generateParent(IChildResource resource) =>
            findInBaseline(resource.Parent) switch
            {
                { Count: 0 } => Option.None,
                var parents => Gen.OneOfConst([.. parents])
            };

        Option<Gen<ModelNodeSet>> generateCompositePredecessors(ICompositeResource resource)
        {
            var primaries = findInBaseline(resource.Primary);
            var secondaries = resource switch
            {
                // For product APIs, the API must be the current revision
                ProductApiResource =>
                    ModelNodeSet.From([.. findInBaseline(resource.Secondary)
                                            .Where(node => ApiRevisionModule.IsRootName(node.Model.Name))]),
                // For gateway APIs, the API must be the current revision
                GatewayApiResource =>
                    ModelNodeSet.From([.. findInBaseline(resource.Secondary)
                                            .Where(node => ApiRevisionModule.IsRootName(node.Model.Name))]),
                _ => findInBaseline(resource.Secondary)
            };

            return primaries.Count == 0 || secondaries.Count == 0
                    ? Option.None
                    : from primary in Gen.OneOfConst([.. primaries])
                      from secondary in Gen.OneOfConst([.. secondaries])
                      select ModelNodeSet.From([primary, secondary]);
        }

        Option<Gen<ModelNodeSet>> generateMandatoryReferences(IResourceWithReference resource)
        {
            var referenceResources = resource.MandatoryReferencedResourceDtoProperties.Keys;
            var referenceModels = referenceResources.Select(findInBaseline).ToImmutableArray();
            if (referenceModels.Any(set => set.Count == 0))
            {
                return Option.None;
            }

            return from nodes in Generator.Traverse(referenceModels, set => Gen.OneOfConst([.. set]))
                   select ModelNodeSet.From([.. nodes]);
        }

        Gen<ModelNodeSet> generateOptionalReferences(IResourceWithReference resource)
        {
            var optionalReferences = resource.OptionalReferencedResourceDtoProperties.Keys;
            var referenceModels = optionalReferences.Select(findInBaseline);

            return from items in Generator.Traverse(referenceModels,
                                                    set => set.Count == 0
                                                            ? Gen.Const(Option<ModelNode>.None())
                                                            : Gen.OneOfConst([.. set]).OptionOf())
                   let itemsWithValues = items.Choose(x => x)
                   select ModelNodeSet.From(itemsWithValues);
        }

        ModelNodeSet findInBaseline(IResource resource) =>
            baseline.Find(resource)
                    .IfNone(() => ModelNodeSet.Empty);

        Option<Gen<ModelNodeSet>> mergeNodeGenerator(Option<Gen<ModelNodeSet>> currentOption, Option<Gen<ModelNode>> newOption) =>
            from currentGen in currentOption
            from newGen in newOption
            select from current in currentGen
                   from @new in newGen
                   select ModelNodeSet.From([.. current, @new]);

        Option<Gen<ModelNodeSet>> mergeNodesGenerator(Option<Gen<ModelNodeSet>> currentOption, Option<Gen<ModelNodeSet>> newOption) =>
            from currentGen in currentOption
            from newGen in newOption
            select from current in currentGen
                   from @new in newGen
                   select ModelNodeSet.From([.. current, .. @new]);
    }
}