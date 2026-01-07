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
        ConfigureIsValidationStrict(builder);

        builder.TryAddSingleton(ResolveGetRelationships);
    }

    internal static GetRelationships ResolveGetRelationships(IServiceProvider provider)
    {
        var parseFile = provider.GetRequiredService<ParseResourceFile>();
        var getDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();
        var isValidationStrict = provider.GetRequiredService<IsValidationStrict>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (operations, cancellationToken) =>
        {
            var pairs = new ConcurrentBag<(ResourceKey Predecessor, ResourceKey Successor)>();

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
                           }, maxDegreeOfParallelism: Option.None, cancellationToken);

            var relationships = Relationships.From(pairs, cancellationToken);

            var fileSystemResourceKeys = ImmutableHashSet.CreateRange(from resourceKeys in resources.Values
                                                                      from resourceKey in resourceKeys
                                                                      select resourceKey);

            validateRelationships(relationships, fileSystemResourceKeys.Contains, cancellationToken);

            return relationships;
        };

        void validateRelationships(Relationships relationships, Func<ResourceKey, bool> resourceInSnapshot, CancellationToken cancellationToken)
        {
            var exceptions = new List<string>();

            relationships.Validate(resourceInSnapshot, cancellationToken)
                         .Iter(error =>
                         {
                             switch (error)
                             {
                                 case ValidationError.Cycle cycle:
                                     exceptions.Add($"Found cycle in resource relationships: {string.Join(" -> ", cycle.Path)}.");
                                     break;
                                 case ValidationError.MissingPredecessor missingPredecessor:
                                     // Skip predecessors that shouldn't be validated
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
        }

        // Some resources don't need validation because they're never extracted
        bool shouldValidate(ResourceKey resourceKey) =>
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