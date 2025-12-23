using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Relationships> GetCurrentRelationships(CancellationToken cancellationToken);
internal delegate ValueTask<Relationships> GetPreviousRelationships(CancellationToken cancellationToken);
internal delegate ValueTask<Relationships> GetRelationships(FileOperations fileOperations, CancellationToken cancellationToken);

internal sealed record Relationships
{
    private Relationships(IEnumerable<KeyValuePair<ResourceKey, ImmutableHashSet<ResourceKey>>> predecessors,
                          IEnumerable<KeyValuePair<ResourceKey, ImmutableHashSet<ResourceKey>>> successors) =>
        (Predecessors, Successors) = (predecessors.ToImmutableDictionary(),
                                      successors.ToImmutableDictionary());

    public ImmutableDictionary<ResourceKey, ImmutableHashSet<ResourceKey>> Predecessors { get; }
    public ImmutableDictionary<ResourceKey, ImmutableHashSet<ResourceKey>> Successors { get; }

    public static Relationships Empty { get; } = new([], []);

    public static Relationships From(IEnumerable<(ResourceKey Predecessor, Option<ResourceKey> Successor)> pairs, CancellationToken cancellationToken)
    {
        var predecessorsBuilder = new Dictionary<ResourceKey, List<ResourceKey>>();
        var successorsBuilder = new Dictionary<ResourceKey, List<ResourceKey>>();

        pairs.Iter(pair =>
        {
            var (predecessor, successorOption) = pair;

            successorOption.Match(successor =>
                                  {
                                      if (predecessorsBuilder.TryGetValue(successor, out var predecessors))
                                      {
                                          predecessors.Add(predecessor);
                                      }
                                      else
                                      {
                                          predecessorsBuilder[successor] = [predecessor];
                                      }
                                  },
                                  () =>
                                  {
                                      // Ensure the predecessor is in the predecessors dictionary even if it has no predecessors
                                      if (predecessorsBuilder.ContainsKey(predecessor) is false)
                                      {
                                          predecessorsBuilder[predecessor] = [];
                                      }
                                  });

            if (successorsBuilder.TryGetValue(predecessor, out var successors))
            {
                successorOption.Iter(successors.Add);
            }
            else
            {
                successorsBuilder[predecessor] = successorOption.Map(successor => new List<ResourceKey>([successor]))
                                                                .IfNone(() => []);
            }
        }, cancellationToken);

        var predecessors = predecessorsBuilder.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableHashSet());
        var successors = successorsBuilder.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableHashSet());

        Validate(predecessors, successors, cancellationToken);

        return new(predecessors, successors);
    }

    private static void Validate(IDictionary<ResourceKey, ImmutableHashSet<ResourceKey>> predecessors, IDictionary<ResourceKey, ImmutableHashSet<ResourceKey>> successors, CancellationToken cancellationToken)
    {
        var errors = new HashSet<string>();

        ImmutableHashSet<ResourceKey> resources = [.. predecessors.Keys,
                                                   .. predecessors.SelectMany(kvp => kvp.Value),
                                                   .. successors.Keys,
                                                   .. successors.SelectMany(kvp => kvp.Value)];

        validateResourcesAreInBothDictionaries();
        validateAllReferencesAreMutual();
        validateNoCycles();

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Found at least one validation error:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }

        void validateResourcesAreInBothDictionaries()
        {
            resources.Iter(resource =>
            {
                if (predecessors.ContainsKey(resource) is false)
                {
                    errors.Add($"Resource '{resource}' has no predecessor registration.");
                }

                if (successors.ContainsKey(resource) is false)
                {
                    errors.Add($"Resource '{resource}' has no successor registration.");
                }
            }, cancellationToken);
        }

        void validateAllReferencesAreMutual()
        {
            resources.Iter(resource =>
            {
                predecessors.Find(resource)
                            .Iter(resourcePredecessors => resourcePredecessors.Iter(predecessor =>
                            {
                                if (successors.TryGetValue(predecessor, out var predecessorSuccessors) is false)
                                {
                                    errors.Add($"Predecessor '{predecessor}' of resource '{resource}' has no successor registration.");
                                }
                                else if (predecessorSuccessors.Contains(resource) is false)
                                {
                                    errors.Add($"Predecessor '{predecessor}' of resource '{resource}' does not list it as a successor.");
                                }
                            }));

                successors.Find(resource)
                          .Iter(resourceSuccessors => resourceSuccessors.Iter(successor =>
                          {
                              if (predecessors.TryGetValue(successor, out var successorPredecessors) is false)
                              {
                                  errors.Add($"Successor '{successor}' of resource '{resource}' has no predecessor registration.");
                              }
                              else if (successorPredecessors.Contains(resource) is false)
                              {
                                  errors.Add($"Successor '{successor}' of resource '{resource}' does not list it as a predecessor.");
                              }
                          }));
            }, cancellationToken);
        }

        void validateNoCycles()
        {
            // Skip validation if we already have errors
            if (errors.Count > 0)
            {
                return;
            }

            // Use a depth-first search to find cycles            
            var states = new Dictionary<ResourceKey, byte>();  //1 = visiting (in current stack), 2 = done
            var path = new Stack<ResourceKey>();

            foreach (var resource in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip the resource if already visited
                if (states.ContainsKey(resource))
                {
                    continue;
                }

                if (doesCycleExist(resource))
                {
                    return;
                }
            }

            bool doesCycleExist(ResourceKey resource)
            {
                if (states.TryGetValue(resource, out var state))
                {
                    if (state == 1)
                    {
                        // Found a cycle
                        var cyclePath = path.Reverse()
                                            .SkipWhile(pathResource => pathResource != resource)
                                            .Append(resource);

                        errors.Add($"Found a cycle: {string.Join(" -> ", cyclePath)}");

                        return true;
                    }
                    else
                    {
                        return false; // Already fully processed
                    }
                }

                states[resource] = 1; // Mark as visiting
                path.Push(resource);

                var currentSuccessors = successors.Find(resource).IfNone(() => []);
                foreach (var successor in currentSuccessors)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (doesCycleExist(successor))
                    {
                        return true;
                    }
                }

                states[resource] = 2; // Mark as done
                path.Pop();

                return false;
            }
        }
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

    private static GetCurrentRelationships ResolveGetCurrentRelationships(IServiceProvider provider)
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

    private static GetPreviousRelationships ResolveGetPreviousRelationships(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getPreviousCommitFileOperations = provider.GetRequiredService<GetPreviousCommitFileOperations>();
        var getRelationships = provider.GetRequiredService<GetRelationships>();

        return async cancellationToken =>
        {
            // No commit ID was passed, so we can't access history
            if (commitIdWasPassed() is false)
            {
                return Relationships.Empty;
            }

            // If we can get the previous commit ID, map previous relationships
            var fileOperationsOption = getPreviousCommitFileOperations();

            var relationshipsOption = await fileOperationsOption.MapTask(async operations => await getRelationships(operations, cancellationToken));

            // Otherwise, return an empty set of relationships
            return relationshipsOption.IfNone(() => Relationships.Empty);
        };
    }

    public static void ConfigureGetRelationships(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureParseResourceFile(builder);
        common.ResourceModule.ConfigureGetInformationFileDto(builder);
        common.ResourceModule.ConfigureGetPolicyFileContents(builder);

        builder.TryAddSingleton(ResolveGetRelationships);
    }

    private static GetRelationships ResolveGetRelationships(IServiceProvider provider)
    {
        var parseFile = provider.GetRequiredService<ParseResourceFile>();
        var getDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();

        return async (operations, cancellationToken) =>
        {
            var pairs = new ConcurrentBag<(ResourceKey Predecessor, Option<ResourceKey> SuccessorOption)>();

            // Build resource dictionary
            var resources = await operations.EnumerateServiceDirectoryFiles()
                                            .Choose(async file => await parseFile(file, operations.ReadFile, cancellationToken))
                                            .GroupBy(key => key.Resource)
                                            .ToDictionaryAsync(group => group.Key,
                                                               group => group.ToImmutableHashSet(),
                                                               cancellationToken: cancellationToken);

            // Build resource relationship pairs
            await resources.Values
                           .SelectMany(group => group)
                           .IterTaskParallel(async key =>
                           {
                               // Process child resources
                               if (key.Resource is IChildResource childResource)
                               {
                                   var parent = getParent(childResource, key.Name, key.Parents);

                                   pairs.Add((parent, key));
                               }

                               // Get DTO JSON from information file resources
                               var dtoJsonOption = Option<JsonObject>.None();
                               if (key.Resource is IResourceWithInformationFile resourceWithInformationFile)
                               {
                                   dtoJsonOption = await getDto(resourceWithInformationFile, key.Name, key.Parents, operations.ReadFile, operations.GetSubDirectories, cancellationToken);
                               }

                               // Process composite resources
                               if (key.Resource is ICompositeResource compositeResource)
                               {
                                   var primary = getPrimary(compositeResource, key.Name, key.Parents);
                                   pairs.Add((primary, key));

                                   var dto = dtoJsonOption.IfNone(() => throw new InvalidOperationException($"Could not get DTO for resource '{key}'."));

                                   var secondary = getSecondary(compositeResource, key.Name, key.Parents, dto)
                                                       .IfNone(() => throw new InvalidOperationException($"Could not get secondary resource for composite resource '{key}'."));
                                   pairs.Add((secondary, key));
                               }

                               // Process resources with references
                               if (key.Resource is IResourceWithReference resourceWithReference)
                               {
                                   var dto = dtoJsonOption.IfNone(() => throw new InvalidOperationException($"Could not get DTO for resource '{key}'."));

                                   getReferences(resourceWithReference, key.Name, key.Parents, dto, cancellationToken)
                                       .Iter(reference => pairs.Add((reference, key)));
                               }

                               // Process policies for named value references
                               if (key.Resource is IPolicyResource policyResource)
                               {
                                   var namedValues = await getPolicyNamedValues(policyResource, key.Name, key.Parents, operations.ReadFile, resources, cancellationToken);
                                   namedValues.Iter(namedValue => pairs.Add((namedValue, key)), cancellationToken);
                               }

                               // Process backend pools for individual backend references
                               if (key.Resource is BackendResource backendResource)
                               {
                                   dtoJsonOption.Map(json => getBackendPoolBackends(backendResource, key.Name, key.Parents, json, cancellationToken))
                                                .IfNone(() => [])
                                                .Iter(backend => pairs.Add((backend, key)), cancellationToken);
                               }

                               // Process workspace backend pools for individual backend references
                               if (key.Resource is WorkspaceBackendResource workspaceBackendResource)
                               {
                                   dtoJsonOption.Map(json => getWorkspaceBackendPoolBackends(workspaceBackendResource, key.Name, key.Parents, json, cancellationToken))
                                                .IfNone(() => [])
                                                .Iter(backend => pairs.Add((backend, key)), cancellationToken);
                               }

                               // Process non-root API revisions
                               if (key.Resource is ApiResource)
                               {
                                   ApiRevisionModule.Parse(key.Name)
                                                    .Iter(x =>
                                                    {
                                                        var (rootName, _) = x;
                                                        var currentRevision = new ResourceKey
                                                        {
                                                            Resource = key.Resource,
                                                            Name = rootName,
                                                            Parents = key.Parents
                                                        };

                                                        pairs.Add((currentRevision, key));
                                                    });
                               }

                               // Process non-root workspace API revisions
                               if (key.Resource is WorkspaceApiResource)
                               {
                                   ApiRevisionModule.Parse(key.Name)
                                                    .Iter(x =>
                                                    {
                                                        var (rootName, _) = x;
                                                        var currentRevision = new ResourceKey
                                                        {
                                                            Resource = key.Resource,
                                                            Name = rootName,
                                                            Parents = key.Parents
                                                        };

                                                        pairs.Add((currentRevision, key));
                                                    });
                               }

                               // Add the resource with no successor. Previous registrations will still be preserved.
                               pairs.Add((key, Option.None));
                           }, maxDegreeOfParallelism: Option.None, cancellationToken);

            // Some resources are not required to have artifacts.
            // For example, it's valid to have '/products/productA/groups/administrators/productGroupInformation.json'
            // without '/groups/administrators/groupInformation.json' since the extractor skips the administrators group
            var predecessorsWithOptionalArtifacts = pairs.Where(pair => pair.SuccessorOption.IsSome)
                                                         .Select(pair => pair.Predecessor)
                                                         .Where(isPredecessorWithOptionalArtifacts)
                                                         .ToImmutableHashSet();

            predecessorsWithOptionalArtifacts.Iter(key => pairs.Add((key, Option.None)), cancellationToken);

            // Build relationships
            return Relationships.From(pairs, cancellationToken);
        };

        static ResourceKey getParent(IChildResource resource, ResourceName name, ParentChain parents) =>
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

        static ResourceKey getPrimary(ICompositeResource resource, ResourceName name, ParentChain parents) =>
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

        static Option<ResourceKey> getSecondary(ICompositeResource resource, ResourceName name, ParentChain parents, JsonObject dto)
        {
            Option<ResourceName> secondaryNameOption = Option.None;
            if (resource is ILinkResource linkResource)
            {
                // For link resources, the secondary name is in the DTO
                var resourceKey = new ResourceKey
                {
                    Resource = resource,
                    Name = name,
                    Parents = parents
                };

                var result = from properties in dto.GetJsonObjectProperty("properties")
                             from id in properties.GetStringProperty(linkResource.DtoPropertyNameForLinkedResource)
                             from resourceName in ResourceName.From(id.Split('/').Last())
                             select resourceName;

                secondaryNameOption = result.ToOption();
            }
            else
            {
                // Otherwise, the secondary name is the same as the resource name
                secondaryNameOption = Option.Some(name);
            }

            return secondaryNameOption.Map(name => new ResourceKey
            {
                Resource = resource.Secondary,
                Name = name,
                Parents = ParentChain.From(parents.SkipLast(1))
            });
        }

        static ImmutableHashSet<ResourceKey> getReferences(IResourceWithReference resource, ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken)
        {
            var resourceKey = new ResourceKey
            {
                Resource = resource,
                Name = name,
                Parents = parents
            };

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
                select new ResourceKey
                {
                    Resource = referencedResource,
                    Name = resourceName,
                    Parents = referencedResourceParents
                };

            // We assume that the referenced resource's predecessors are a subset of the current resource's parent chain
            Result<ParentChain> getReferencedResourceParents(IResource referencedResource) =>
                referencedResource.GetTraversalPredecessorHierarchy()
                                  .Traverse(predecessor => parents.Head(pair => pair.Resource == predecessor)
                                                                  .Match(pair => Result.Success(pair),
                                                                         () => Error.From($"Could not find resource {predecessor.GetType().Name} in parent chain of {resourceKey}.")),
                                            cancellationToken)
                                  .Map(pairs => ParentChain.From(pairs));

            // We expect that the ID has the format /{serviceResourceId (optional)}/{parents.ToResourceId()}/resource.CollectionUriPath/name
            static Result<ResourceName> getResourceName(string id, IResource resource, ParentChain parents) =>
                id.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
                {
                    [.. var head, var collectionUriPath, var name] when
                        // Collection URI paths match
                        collectionUriPath.Equals(resource.CollectionUriPath, StringComparison.OrdinalIgnoreCase)
                        // Parents match
                        && string.Join('/', head).Trim('/').EndsWith(parents.ToResourceId().Trim('/'), StringComparison.OrdinalIgnoreCase) => ResourceName.From(name),
                    _ => Error.From($"Expected '{id}' to match the format '...{parents.ToResourceId()}/{resource.CollectionUriPath}/{{name}}'.")
                };
        }

        async ValueTask<ImmutableHashSet<ResourceKey>> getPolicyNamedValues(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources, CancellationToken cancellationToken)
        {
            var workspaceOption = parents.Head(tuple => tuple.Resource is WorkspaceResource);

            var namedValueNames = await getPolicyNamedValueNames(resource, name, parents, readFile, resources, cancellationToken);

            return [.. namedValueNames.Choose(name => findNamedValue(name, workspaceOption, resources))];
        }

        async ValueTask<ImmutableHashSet<ResourceName>> getPolicyNamedValueNames(IPolicyResource resource, ResourceName name, ParentChain parents, ReadFile readFile, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources, CancellationToken cancellationToken)
        {
            var contentsOption = from binaryData in await getPolicyFileContents(resource, name, parents, readFile, cancellationToken)
                                 select binaryData.ToString();

            var contents = contentsOption.IfNoneNull() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(contents))
            {
                return [];
            }

            var regex = new Regex("{{\\s*(.*?)\\s*}}", RegexOptions.CultureInvariant);

            return [.. regex.Matches(contents)
                            .Choose(match => ResourceName.From(match.Groups[1].Value.Trim())
                                                         .ToOption())];
        }

        Option<ResourceKey> findNamedValue(ResourceName name, Option<(IResource, ResourceName)> workspaceOption, IDictionary<IResource, ImmutableHashSet<ResourceKey>> resources) =>
            // Look for the named value in the workspace
            workspaceOption.Bind(workspace => from namedValues in resources.Find(WorkspaceNamedValueResource.Instance)
                                              from namedValue in namedValues.Head(key => key.Name == name
                                                                                         && key.Parents.Any(parent => parent == workspace))
                                              select namedValue)
                           // If not found, look for a service-level named value
                           .IfNone(() => from namedValues in resources.Find(NamedValueResource.Instance)
                                         from namedValue in namedValues.Head(key => key.Name == name)
                                         select namedValue);

        static ImmutableHashSet<ResourceKey> getBackendPoolBackends(BackendResource resource, ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken)
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
                                     .Select(backendName => new ResourceKey
                                     {
                                         Resource = resource,
                                         Name = backendName,
                                         Parents = parents
                                     })];
        }

        static ImmutableHashSet<ResourceKey> getWorkspaceBackendPoolBackends(WorkspaceBackendResource resource, ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken)
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
                                     .Select(backendName => new ResourceKey
                                     {
                                         Resource = resource,
                                         Name = backendName,
                                         Parents = parents
                                     })];
        }

        static bool isPredecessorWithOptionalArtifacts(ResourceKey key) =>
            key.Resource switch
            {
                ApiOperationResource => true,
                WorkspaceResource => true,
                WorkspaceApiOperationResource => true,
                GroupResource
                    when key.Name == GroupResource.Administrators
                         || key.Name == GroupResource.Developers
                         || key.Name == GroupResource.Guests => true,
                WorkspaceGroupResource
                    when key.Name == WorkspaceGroupResource.Administrators
                         || key.Name == WorkspaceGroupResource.Developers
                         || key.Name == WorkspaceGroupResource.Guests => true,
                _ => false
            };
    }
}