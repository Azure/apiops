using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Relationships> GetCurrentRelationships(CancellationToken cancellationToken);
internal delegate ValueTask<Relationships> GetPreviousRelationships(CancellationToken cancellationToken);
internal delegate ValueTask<Relationships> GetRelationships(FileOperations fileOperations, CancellationToken cancellationToken);
internal delegate ValueTask<ImmutableDictionary<IResource, ImmutableHashSet<ResourceKey>>> BuildResourceMap(FileOperations fileOperations, CancellationToken cancellationToken);
internal delegate ValueTask<ImmutableHashSet<(ResourceKey Predecessor, ResourceKey Successor)>> GetRelationshipPairs(ImmutableDictionary<IResource, ImmutableHashSet<ResourceKey>> resources, FileOperations fileOperations, CancellationToken cancellationToken);
internal delegate void ValidateRelationshipGraph(Relationships relationships, ImmutableDictionary<IResource, ImmutableHashSet<ResourceKey>> resources, CancellationToken cancellationToken);
internal delegate bool IsValidationStrict();

internal abstract record ValidationError
{
    internal sealed record Cycle : ValidationError
    {
        public required ImmutableArray<ResourceKey> Path { get; init; }
    }

    internal sealed record MissingPredecessor : ValidationError
    {
        public required ResourceKey Predecessor { get; init; }
        public required ResourceKey Successor { get; init; }
    }
}

internal sealed record Relationships
{
    public ImmutableDictionary<ResourceKey, ImmutableHashSet<ResourceKey>> Predecessors { get; }
    public ImmutableDictionary<ResourceKey, ImmutableHashSet<ResourceKey>> Successors { get; }

    private Relationships(IEnumerable<KeyValuePair<ResourceKey, ImmutableHashSet<ResourceKey>>> predecessors,
                          IEnumerable<KeyValuePair<ResourceKey, ImmutableHashSet<ResourceKey>>> successors) =>
        (Predecessors, Successors) = (predecessors.ToImmutableDictionary(),
                                      successors.ToImmutableDictionary());

    public static Relationships Empty { get; } = new([], []);

    public static Relationships From(IEnumerable<(ResourceKey Predecessor, ResourceKey Successor)> pairs, CancellationToken cancellationToken)
    {
        var predecessors = new Dictionary<ResourceKey, ImmutableHashSet<ResourceKey>>();
        var successors = new Dictionary<ResourceKey, ImmutableHashSet<ResourceKey>>();

        pairs.Iter(pair =>
        {
            var (predecessor, successor) = pair;

            if (predecessors.TryGetValue(successor, out var predecessorSet))
            {
                predecessors[successor] = predecessorSet.Add(predecessor);
            }
            else
            {
                predecessors[successor] = [predecessor];
            }

            if (successors.TryGetValue(predecessor, out var successorSet))
            {
                successors[predecessor] = successorSet.Add(successor);
            }
            else
            {
                successors[predecessor] = [successor];
            }
        }, cancellationToken);

        return new(predecessors, successors);
    }

    public ImmutableArray<ValidationError> Validate(Func<ResourceKey, bool> resourceInSnapshot, CancellationToken cancellationToken) =>
        [ .. GetMissingPredecessors(resourceInSnapshot),
          .. GetCycles(cancellationToken) ];

    private IEnumerable<ValidationError.MissingPredecessor> GetMissingPredecessors(Func<ResourceKey, bool> resourceInSnapshot) =>
        Predecessors.SelectMany(kvp => from predecessor in kvp.Value
                                       let successor = kvp.Key
                                       where resourceInSnapshot(successor) && resourceInSnapshot(predecessor) is false
                                       select new ValidationError.MissingPredecessor
                                       {
                                           Predecessor = predecessor,
                                           Successor = successor
                                       });

    private IEnumerable<ValidationError.Cycle> GetCycles(CancellationToken cancellationToken)
    {
        var nodes = ImmutableHashSet.CreateRange([.. Predecessors.Keys,
                                                  .. Predecessors.SelectMany(kvp => kvp.Value),
                                                  .. Successors.Keys,
                                                  .. Successors.SelectMany(kvp => kvp.Value)]);

        IEnumerable<ResourceKey> getSuccessors(ResourceKey resource) =>
            Successors.Find(resource)
                      .IfNone(() => []);

        return from component in ResourceGraph.GetStronglyConnectedComponents(nodes, getSuccessors, cancellationToken)
                   // Keep components that indicate a cycle
               where component.ToImmutableArray() switch
               {
                   [] => false,
                   [var key] => getSuccessors(key).Contains(key),
                   _ => true
               }
               let cycleOption = ResourceGraph.FindCycle(component,
                                                         // Only traverse resources within the strongly connected component
                                                         resource => getSuccessors(resource).Where(component.Contains),
                                                         cancellationToken)
               select cycleOption
                        .Select(cycle => new ValidationError.Cycle
                        {
                            Path = [.. cycle]
                        })
                        .IfNoneThrow(() => new InvalidOperationException("Expected to find a cycle in strongly connected component."));
    }
}

internal static class RelationshipsModule
{
    public static void ConfigureGetCurrentRelationships(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureGetCurrentCommitFileOperations(builder);
        FileSystemModule.ConfigureGetLocalFileOperations(builder);
        ConfigureGetRelationships(builder);

        builder.TryAddSingleton(ResolveGetCurrentRelationships);
    }

    internal static GetCurrentRelationships ResolveGetCurrentRelationships(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getCurrentCommitFileOperations = provider.GetRequiredService<GetCurrentCommitFileOperations>();
        var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();
        var getRelationships = provider.GetRequiredService<GetRelationships>();

        return async cancellationToken =>
        {
            var fileOperations = commitIdWasPassed()
                                    ? getCurrentCommitFileOperations()
                                        .IfNone(() => throw new InvalidOperationException("Cannot get file operations for current commit."))
                                    : getLocalFileOperations();

            return await getRelationships(fileOperations, cancellationToken);
        };
    }

    public static void ConfigureGetPreviousRelationships(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureGetPreviousCommitFileOperations(builder);
        ConfigureGetRelationships(builder);

        builder.TryAddSingleton(ResolveGetPreviousRelationships);
    }

    internal static GetPreviousRelationships ResolveGetPreviousRelationships(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getPreviousCommitFileOperations = provider.GetRequiredService<GetPreviousCommitFileOperations>();
        var getRelationships = provider.GetRequiredService<GetRelationships>();

        return async cancellationToken =>
        {
            if (commitIdWasPassed() is false)
            {
                // No commit ID was passed, so we can't access history
                return Relationships.Empty;
            }
            else
            {
                // If we can get the previous commit ID, map previous relationships
                var fileOperationsOption = getPreviousCommitFileOperations();
                var relationshipsOption = await fileOperationsOption.MapTask(async operations => await getRelationships(operations, cancellationToken));

                // Fall back to an empty set of relationships
                return relationshipsOption.IfNone(() => Relationships.Empty);
            }
        };
    }

    public static void ConfigureGetRelationships(IHostApplicationBuilder builder)
    {
        ConfigureBuildResourceMap(builder);
        ConfigureGetRelationshipPairs(builder);
        ConfigureValidateRelationshipGraph(builder);

        builder.TryAddSingleton(ResolveGetRelationships);
    }

    internal static GetRelationships ResolveGetRelationships(IServiceProvider provider)
    {
        var buildResourceMap = provider.GetRequiredService<BuildResourceMap>();
        var getRelationshipPairs = provider.GetRequiredService<GetRelationshipPairs>();
        var validateRelationshipGraph = provider.GetRequiredService<ValidateRelationshipGraph>();

        return async (operations, cancellationToken) =>
        {
            var resources = await buildResourceMap(operations, cancellationToken);
            var pairs = await getRelationshipPairs(resources, operations, cancellationToken);
            var relationships = Relationships.From(pairs, cancellationToken);

            validateRelationshipGraph(relationships, resources, cancellationToken);

            return relationships;
        };
    }

    public static void ConfigureBuildResourceMap(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureParseResourceFile(builder);

        builder.TryAddSingleton(ResolveBuildResourceMap);
    }

    private static BuildResourceMap ResolveBuildResourceMap(IServiceProvider provider)
    {
        var parseFile = provider.GetRequiredService<ParseResourceFile>();

        return async (fileOperations, cancellationToken) =>
        [
            .. await fileOperations.EnumerateServiceDirectoryFiles()
                                   .Choose(async file => await parseFile(file, fileOperations.ReadFile, cancellationToken))
                                   .GroupBy(key => key.Resource)
                                   .ToDictionaryAsync(group => group.Key,
                                                      group => group.ToImmutableHashSet(),
                                                      cancellationToken: cancellationToken)
        ];
    }

    public static void ConfigureGetRelationshipPairs(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureGetInformationFileDto(builder);
        common.ResourceModule.ConfigureGetPolicyFileContents(builder);
        ConfigurationModule.ConfigureGetConfigurationOverride(builder);

        builder.TryAddSingleton(ResolveGetRelationshipPairs);
    }

    private static GetRelationshipPairs ResolveGetRelationshipPairs(IServiceProvider provider)
    {
        var getDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();
        var getConfigurationOverride = provider.GetRequiredService<GetConfigurationOverride>();

        var namedValueTokenRegex = new Regex("{{\\s*(.*?)\\s*}}", RegexOptions.CultureInvariant);
        var includeFragmentRegex = new Regex("<include-fragment\\s+fragment-id=\"([^\"]+)\"\\s*/>", RegexOptions.CultureInvariant);
        var policyBackendIdRegex = new Regex("<set-backend-service\\b[^>]*\\bbackend-id\\s*=\\s*[\"']([^\"']+)[\"'][^>]*/?>", RegexOptions.CultureInvariant);

        return async (resources, operations, cancellationToken) =>
        {
            var pairs = new ConcurrentBag<(ResourceKey Predecessor, ResourceKey Successor)>();

            ImmutableDictionary<IResourceWithInformationFile, ImmutableDictionary<string, ResourceKey>> displayNameDictionary = [
                .. await resources.Where(kvp => kvp.Key is NamedValueResource or WorkspaceNamedValueResource)
                                  .Choose(async kvp =>
                                  {
                                      var (resource, keys) = kvp;

                                      if (resource is IResourceWithInformationFile resourceWithInformationFile)
                                      {
                                          ImmutableDictionary<string, ResourceKey> dictionary =
                                              [.. await keys.Choose(async key =>
                                              {
                                                  var (name, parents) = (key.Name, key.Parents);

                                                  var dtoOption = await getDto(resourceWithInformationFile, name, parents, operations.ReadFile, operations.GetSubDirectories, cancellationToken);
                                                  await dtoOption.IterTask(async dto =>
                                                  {
                                                      var configurationOverrideOption = await getConfigurationOverride(key, cancellationToken);
                                                      configurationOverrideOption.Iter(configurationOverride => dtoOption = dto.MergeWith(configurationOverride));
                                                  });

                                                  return from dto in dtoOption
                                                         from properties in dto.GetJsonObjectProperty("properties").ToOption()
                                                         from displayName in properties.GetStringProperty("displayName").ToOption()
                                                         select KeyValuePair.Create(displayName, key);
                                              }).ToArrayAsync(cancellationToken)];

                                          return Option.Some(KeyValuePair.Create(resourceWithInformationFile, dictionary));
                                      }
                                      else
                                      {
                                          return Option.None;
                                      }
                                  })
                                  .ToArrayAsync(cancellationToken)
            ];

            await resources.Values
                           .SelectMany(group => group)
                           .IterTaskParallel(async key =>
                           {
                               if (key.Resource is IChildResource childResource)
                               {
                                   var parent = getParent(childResource, key.Name, key.Parents);
                                   pairs.Add((parent, key));
                               }

                               var dtoJsonOption = Option<JsonObject>.None();
                               if (key.Resource is IResourceWithInformationFile resourceWithInformationFile)
                               {
                                   dtoJsonOption = await getDto(resourceWithInformationFile, key.Name, key.Parents, operations.ReadFile, operations.GetSubDirectories, cancellationToken);
                               }

                               // Merge any configuration overrides to ensure relationships are properly captured
                               await dtoJsonOption.IterTask(async dto =>
                               {
                                   var configurationOverrideOption = await getConfigurationOverride(key, cancellationToken);
                                   configurationOverrideOption.Iter(configurationOverride => dtoJsonOption = dto.MergeWith(configurationOverride));
                               });

                               if (key.Resource is ICompositeResource compositeResource)
                               {
                                   var primary = getPrimary(compositeResource, key.Name, key.Parents);
                                   pairs.Add((primary, key));

                                   var dto = dtoJsonOption.IfNone(() => throw new InvalidOperationException($"Could not get DTO for resource '{key}'."));

                                   var secondary = getSecondary(compositeResource, key.Name, key.Parents, dto)
                                                       .IfNone(() => throw new InvalidOperationException($"Could not get secondary resource for composite resource '{key}'."));
                                   pairs.Add((secondary, key));
                               }

                               if (key.Resource is IResourceWithReference resourceWithReference)
                               {
                                   var dto = dtoJsonOption.IfNone(() => throw new InvalidOperationException($"Could not get DTO for resource '{key}'."));

                                   getReferences(resourceWithReference, key.Name, key.Parents, dto, cancellationToken)
                                       .Iter(reference => pairs.Add((reference, key)));
                               }

                               if (key.Resource is IPolicyResource policyResource)
                               {
                                   var namedValues = await getPolicyNamedValues(policyResource, key.Name, key.Parents, operations.ReadFile, cancellationToken);
                                   namedValues.Iter(namedValue => pairs.Add((namedValue, key)), cancellationToken);
                               }

                               if (key.Resource is IPolicyResource policyResourceForFragments)
                               {
                                   var policyFragments = await getPolicyFragments(policyResourceForFragments, key.Name, key.Parents, operations.ReadFile, resources, cancellationToken);
                                   policyFragments.Iter(fragment => pairs.Add((fragment, key)), cancellationToken);
                               }

                               if (key.Resource is IPolicyResource policyResourceForBackends)
                               {
                                   var policyBackends = await getPolicyBackends(policyResourceForBackends, key.Name, key.Parents, operations.ReadFile, resources, cancellationToken);
                                   policyBackends.Iter(backend => pairs.Add((backend, key)), cancellationToken);
                               }

                               dtoJsonOption.Iter(dtoJson =>
                               {
                                   var content = dtoJson.ToJsonString();
                                   var namedValues = getNamedValuesFromContent(content, key.Parents);
                                   namedValues.Iter(namedValue => pairs.Add((namedValue, key)), cancellationToken);
                               });

                               if (key.Resource is BackendResource backendResource)
                               {
                                   dtoJsonOption.Map(json => getBackendPoolBackends(backendResource, key.Name, key.Parents, json, cancellationToken))
                                                .IfNone(() => [])
                                                .Iter(backend => pairs.Add((backend, key)), cancellationToken);
                               }

                               if (key.Resource is WorkspaceBackendResource workspaceBackendResource)
                               {
                                   dtoJsonOption.Map(json => getWorkspaceBackendPoolBackends(workspaceBackendResource, key.Name, key.Parents, json, cancellationToken))
                                                .IfNone(() => [])
                                                .Iter(backend => pairs.Add((backend, key)), cancellationToken);
                               }

                               if (key.Resource is ApiResource)
                               {
                                   ApiRevisionModule.Parse(key.Name)
                                                    .Iter(x =>
                                                    {
                                                        var (rootName, _) = x;
                                                        var currentRevision = ResourceKey.From(key.Resource, rootName, key.Parents);

                                                        pairs.Add((currentRevision, key));
                                                    });
                               }

                               if (key.Resource is WorkspaceApiResource)
                               {
                                   ApiRevisionModule.Parse(key.Name)
                                                    .Iter(x =>
                                                    {
                                                        var (rootName, _) = x;
                                                        var currentRevision = ResourceKey.From(key.Resource, rootName, key.Parents);

                                                        pairs.Add((currentRevision, key));
                                                    });
                               }

                               if (key.Resource is ApiOperationPolicyResource or WorkspaceApiOperationPolicyResource)
                               {
                                   var (grandParentResource, grandParentName) = (key.Resource, key.Parents.SkipLast(1).LastOrDefault()) switch
                                   {
                                       (ApiOperationPolicyResource, (ApiResource apiResource, var apiName)) =>
                                           (apiResource as IResource, apiName),
                                       (WorkspaceApiOperationPolicyResource, (WorkspaceApiResource workspaceApiResource, var apiName)) =>
                                           (workspaceApiResource, apiName),
                                       (_, (null, _)) =>
                                           throw new InvalidOperationException($"Resource '{key}' is missing its grandparent."),
                                       (_, (var resource, _)) =>
                                           throw new InvalidOperationException($"Resource '{key}' has grandparent resource '{resource.GetType().Name}' instead of the expected API resource.")
                                   };

                                   var grandParent = new ResourceKey
                                   {
                                       Resource = grandParentResource,
                                       Name = grandParentName,
                                       Parents = ParentChain.From(key.Parents.SkipLast(2))
                                   };

                                   pairs.Add((grandParent, key));
                               }

                           }, maxDegreeOfParallelism: Option.None, cancellationToken);

            return [.. pairs];

            async ValueTask<ImmutableHashSet<ResourceKey>> getPolicyNamedValues(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, CancellationToken cancellationToken)
            {
                var contentsOption = await getEffectivePolicyContents(resource, name, parents, readFile, cancellationToken);

                var contents = contentsOption.IfNoneNull() ?? string.Empty;

                return getNamedValuesFromContent(contents, parents);
            }

            // Get policy contents from the policy file and apply any configuration overrides that might exist.
            async ValueTask<Option<string>> getEffectivePolicyContents(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, CancellationToken cancellationToken)
            {
                var fileContentsOption = from binaryData in await getPolicyFileContents(resource, name, parents, readFile, cancellationToken)
                                         select binaryData.ToString();

                return await fileContentsOption.MapTask(async contents =>
                {
                    var overrideOption = from overrideDto in await getConfigurationOverride(ResourceKey.From(resource, name, parents), cancellationToken)
                                         let overrideContentsResult = from properties in overrideDto.GetJsonObjectProperty("properties")
                                                                      from value in properties.GetStringProperty("value")
                                                                      select value
                                         from overrideContents in overrideContentsResult.ToOption()
                                         select overrideContents;

                    return overrideOption.IfNone(() => contents);
                });
            }

            async ValueTask<ImmutableHashSet<ResourceKey>> getPolicyFragments(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources, CancellationToken cancellationToken)
            {
                var contentsOption = await getEffectivePolicyContents(resource, name, parents, readFile, cancellationToken);

                var contents = contentsOption.IfNoneNull() ?? string.Empty;

                return getPolicyFragmentsFromContent(contents, parents, resources);
            }

            async ValueTask<ImmutableHashSet<ResourceKey>> getPolicyBackends(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources, CancellationToken cancellationToken)
            {
                var contentsOption = await getEffectivePolicyContents(resource, name, parents, readFile, cancellationToken);

                var contents = contentsOption.IfNoneNull() ?? string.Empty;

                return getPolicyBackendsFromContent(contents, parents, resources);
            }

            ImmutableHashSet<ResourceKey> getNamedValuesFromContent(string content, ParentChain parents)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    return [];
                }

                return [.. namedValueTokenRegex.Matches(content)
                                               .Select(match => match.Groups[1].Value.Trim())
                                               .Choose(displayName => getNamedValueWithDisplayName(displayName))];

                Option<ResourceKey> getNamedValueWithDisplayName(string displayName) =>
                    parents.Any(tuple => tuple.Resource is WorkspaceResource)
                        ? findResourceKeyWithDisplayName(WorkspaceNamedValueResource.Instance, displayName)
                            .IfNone(() => findResourceKeyWithDisplayName(NamedValueResource.Instance, displayName))
                        : findResourceKeyWithDisplayName(NamedValueResource.Instance, displayName);

                Option<ResourceKey> findResourceKeyWithDisplayName(IResourceWithInformationFile resource, string displayName) =>
                    from dictionary in displayNameDictionary.Find(resource)
                    from resourceKey in dictionary.Find(displayName)
                    select resourceKey;
            }

            ImmutableHashSet<ResourceKey> getPolicyFragmentsFromContent(string content, ParentChain parents, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    return [];
                }

                var workspaceOption = parents.Head(tuple => tuple.Resource is WorkspaceResource);

                var referencedFragments = includeFragmentRegex.Matches(content)
                                                              .Select(match => match.Groups[1].Value.Trim())
                                                              .Choose(nameString => ResourceName.From(nameString).ToOption())
                                                              .Choose(name => findPolicyFragment(name, workspaceOption, resources));

                return [.. referencedFragments];
            }

            Option<ResourceKey> findPolicyFragment(ResourceName name, Option<(IResource, ResourceName)> workspaceOption, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources) =>
                workspaceOption.Bind(workspace => from fragments in resources.Find(WorkspacePolicyFragmentResource.Instance)
                                                  from fragment in fragments.Head(key => key.Name == name
                                                                                          && key.Parents.Any(parent => parent == workspace))
                                                  select fragment)
                               .IfNone(() => from fragments in resources.Find(PolicyFragmentResource.Instance)
                                             from fragment in fragments.Head(key => key.Name == name)
                                             select fragment);

            ImmutableHashSet<ResourceKey> getPolicyBackendsFromContent(string content, ParentChain parents, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    return [];
                }

                var workspaceOption = parents.Head(tuple => tuple.Resource is WorkspaceResource);

                var referencedBackends = policyBackendIdRegex.Matches(content)
                                                             .Select(match => match.Groups[1].Value.Trim())
                                                             .Choose(nameString => ResourceName.From(nameString).ToOption())
                                                             .Choose(name => findBackend(name, workspaceOption, resources));

                return [.. referencedBackends];
            }

            Option<ResourceKey> findBackend(ResourceName name, Option<(IResource, ResourceName)> workspaceOption, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources) =>
                workspaceOption.Bind(workspace => from backends in resources.Find(WorkspaceBackendResource.Instance)
                                                  from backend in backends.Head(key => key.Name == name
                                                                                       && key.Parents.Any(parent => parent == workspace))
                                                  select backend)
                               .IfNone(() => from backends in resources.Find(BackendResource.Instance)
                                             from backend in backends.Head(key => key.Name == name)
                                             select backend);

            ResourceKey getParent(IChildResource resource, ResourceName name, ParentChain parents) =>
                parents.LastOrDefault() switch
                {
                    (null, _) or (_, null) =>
                        throw new InvalidOperationException($"Resource '{resource}' is missing its parent."),
                    (var parentResource, _) when parentResource != resource.Parent =>
                        throw new InvalidOperationException($"Resource '{resource}' has parent '{resource.Parent.GetType().Name}' but its closest ancestor is '{parentResource.GetType().Name}'."),
                    (var parentResource, var parentName) => new ResourceKey
                    {
                        Resource = parentResource,
                        Name = parentName,
                        Parents = ParentChain.From(parents.SkipLast(1))
                    }
                };

            ResourceKey getPrimary(ICompositeResource resource, ResourceName name, ParentChain parents) =>
                parents.LastOrDefault() switch
                {
                    (null, _) or (_, null) =>
                        throw new InvalidOperationException($"Resource '{resource}' is missing its primary."),
                    (var primaryResource, _) when primaryResource != resource.Primary =>
                        throw new InvalidOperationException($"Resource '{resource}' has primary '{resource.Primary.GetType().Name}' but its closest ancestor is '{primaryResource.GetType().Name}'."),
                    (var primaryResource, var primaryName) => new ResourceKey
                    {
                        Resource = primaryResource,
                        Name = primaryName,
                        Parents = ParentChain.From(parents.SkipLast(1))
                    }
                };

            Option<ResourceKey> getSecondary(ICompositeResource resource, ResourceName name, ParentChain parents, JsonObject dto)
            {
                Option<ResourceName> secondaryNameOption = Option.None;
                if (resource is ILinkResource linkResource)
                {
                    var result = from properties in dto.GetJsonObjectProperty("properties")
                                 from id in properties.GetStringProperty(linkResource.DtoPropertyNameForLinkedResource)
                                 from resourceName in ResourceName.From(id.Split('/').Last())
                                 select resourceName;

                    secondaryNameOption = result.ToOption();
                }
                else
                {
                    secondaryNameOption = Option.Some(name);
                }

                return secondaryNameOption.Map(name => new ResourceKey
                {
                    Resource = resource.Secondary,
                    Name = name,
                    Parents = ParentChain.From(parents.SkipLast(1))
                });
            }

            ImmutableHashSet<ResourceKey> getReferences(IResourceWithReference resource, ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken)
            {
                var resourceKey = ResourceKey.From(resource, name, parents);

                var mandatoryReferences = resource.MandatoryReferencedResourceDtoProperties
                                                  .Select(kvp => getReferencedResourceKey(kvp.Key, kvp.Value)
                                                                    .MapError(error => Error.From($"Could not obtain mandatory reference to {kvp.Key.GetType().Name} in property {kvp.Value} of DTO {resourceKey}. {error}"))
                                                                    .IfErrorThrow());

                var optionalReferences = resource.OptionalReferencedResourceDtoProperties
                                                 .Choose(kvp => getReferencedResourceKey(kvp.Key, kvp.Value).ToOption());

                return [.. mandatoryReferences, .. optionalReferences];

                Result<ResourceKey> getReferencedResourceKey(IResource referencedResource, string dtoPropertyName) =>
                    from properties in dto.GetJsonObjectProperty("properties")
                    from id in properties.GetStringProperty(dtoPropertyName)
                    from referencedResourceParents in getReferencedResourceParents(referencedResource)
                    from resourceName in getResourceName(id, referencedResource, referencedResourceParents)
                    select ResourceKey.From(referencedResource, resourceName, referencedResourceParents);

                Result<ParentChain> getReferencedResourceParents(IResource referencedResource) =>
                    referencedResource.GetTraversalPredecessorHierarchy()
                                      .Traverse(predecessor => parents.Head(pair => pair.Resource == predecessor)
                                                                      .Match(pair => Result.Success(pair),
                                                                             () => Error.From($"Could not find resource {predecessor.GetType().Name} in parent chain of {resourceKey}.")),
                                                cancellationToken)
                                      .Map(pairs => ParentChain.From(pairs));

                static Result<ResourceName> getResourceName(string id, IResource resource, ParentChain parents) =>
                    id.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
                    {
                        [.. var head, var collectionUriPath, var resourceName] when
                            collectionUriPath.Equals(resource.CollectionUriPath, StringComparison.OrdinalIgnoreCase)
                            && string.Join('/', head).Trim('/').EndsWith(parents.ToResourceId().Trim('/'), StringComparison.OrdinalIgnoreCase) => ResourceName.From(resourceName),
                        _ => Error.From($"Expected '{id}' to match the format '...{parents.ToResourceId()}/{resource.CollectionUriPath}/{{name}}'.")
                    };
            }

            ImmutableHashSet<ResourceKey> getBackendPoolBackends(BackendResource resource, ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken)
            {
                var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

                return [.. JsonNodeModule.To<BackendDto>(dto, serializerOptions)
                                     .Map(backendDto => backendDto.Properties.Pool?.Services ?? [])
                                     .IfError(_ => [])
                                     .Choose(backend =>
                                     {
                                         var nameString = backend.Id?.Split('/').LastOrDefault() ?? string.Empty;

                                         return ResourceName.From(nameString)
                                                            .ToOption();
                                     })
                                     .Select(backendName => ResourceKey.From(resource, backendName, parents))];
            }

            ImmutableHashSet<ResourceKey> getWorkspaceBackendPoolBackends(WorkspaceBackendResource resource, ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken)
            {
                var serializerOptions = ((IResourceWithDto)resource).SerializerOptions;

                return [.. JsonNodeModule.To<WorkspaceBackendDto>(dto, serializerOptions)
                                     .Map(backendDto => backendDto.Properties.Pool?.Services ?? [])
                                     .IfError(_ => [])
                                     .Choose(backend =>
                                     {
                                         var nameString = backend.Id?.Split('/').LastOrDefault() ?? string.Empty;

                                         return ResourceName.From(nameString)
                                                            .ToOption();
                                     })
                                     .Select(backendName => ResourceKey.From(resource, backendName, parents))];
            }
        };
    }

    public static void ConfigureValidateRelationshipGraph(IHostApplicationBuilder builder)
    {
        ConfigureIsValidationStrict(builder);

        builder.TryAddSingleton(ResolveValidateRelationshipGraph);
    }

    private static ValidateRelationshipGraph ResolveValidateRelationshipGraph(IServiceProvider provider)
    {
        var isValidationStrict = provider.GetRequiredService<IsValidationStrict>();
        var logger = provider.GetRequiredService<ILogger>();

        return (relationships, resources, cancellationToken) =>
        {
            var snapshotKeys = ImmutableHashSet.CreateRange(from resourceKeys in resources.Values
                                                            from resourceKey in resourceKeys
                                                            select resourceKey);

            var exceptions = new List<string>();

            relationships.Validate(snapshotKeys.Contains, cancellationToken)
                         .Iter(error =>
                         {
                             switch (error)
                             {
                                 case ValidationError.Cycle cycle:
                                     exceptions.Add($"Found cycle in resource relationships: {string.Join(" -> ", cycle.Path)}.");
                                     break;
                                 case ValidationError.MissingPredecessor missingPredecessor:
                                     if (shouldValidate(missingPredecessor.Predecessor) is false)
                                     {
                                         break;
                                     }

                                     if (isValidationStrict())
                                     {
                                         exceptions.Add($"Resource '{missingPredecessor.Successor}' is missing its predecessor '{missingPredecessor.Predecessor}'.");
                                     }
                                     else
                                     {
                                         logger.LogWarning("Resource '{Resource}' is missing its predecessor '{Resource}'.", missingPredecessor.Successor, missingPredecessor.Predecessor);
                                     }
                                     break;
                             }
                         }, cancellationToken);

            if (exceptions.Count > 0)
            {
                throw new InvalidOperationException($"Found at least one validation error:{Environment.NewLine}{string.Join(Environment.NewLine, exceptions)}");
            }

            return;

            static bool shouldValidate(ResourceKey resourceKey) =>
                resourceKey.Resource switch
                {
                    SubscriptionResource => resourceKey.Name != SubscriptionResource.Master,
                    GroupResource => resourceKey.Name != GroupResource.Administrators
                                     && resourceKey.Name != GroupResource.Developers
                                     && resourceKey.Name != GroupResource.Guests,
                    ApiOperationResource => false,
                    WorkspaceResource => false,
                    WorkspaceGroupResource => resourceKey.Name != WorkspaceGroupResource.Administrators
                                              && resourceKey.Name != WorkspaceGroupResource.Developers
                                              && resourceKey.Name != WorkspaceGroupResource.Guests,
                    WorkspaceApiOperationResource => false,
                    _ => true
                };
        };
    }

    public static void ConfigureIsValidationStrict(IHostApplicationBuilder builder)
    {
        builder.TryAddSingleton(ResolveIsValidationStrict);
    }

    internal static IsValidationStrict ResolveIsValidationStrict(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return () => configuration.GetValue("STRICT_VALIDATION")
                                  .Map(flag => bool.TryParse(flag, out var result)
                                                ? result
                                                : throw new InvalidOperationException($"'STRICT_VALIDATION' must be 'true' or 'false'."))
                                  .IfNone(() => false);
    }
}
