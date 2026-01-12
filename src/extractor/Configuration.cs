using common;
using DotNext.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

/// <summary>
/// Checks if a resource is in included in the extractor configuration.
/// </summary>
/// <returns>Some(true) if the resource is in configuration, Some(false) if it isn't, and None if no configuration was defined for that resource type.</returns>
internal delegate ValueTask<Option<bool>> ResourceIsInConfiguration(ResourceKey resourceKey, CancellationToken cancellationToken);

internal static class ConfigurationModule
{
    public static void ConfigureResourceIsInConfiguration(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveResourceIsInConfiguration);

    internal static ResourceIsInConfiguration ResolveResourceIsInConfiguration(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var configurationJsonCache = new AsyncLazy<JsonObject>(async cancellationToken => await common.ConfigurationModule.GetJsonObject(configuration, cancellationToken));
        var parentsJsonCache = new ConcurrentDictionary<ParentChain, Option<JsonObject>>();

        return async (resourceKey, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("resource.is.in.configuration")
                                              ?.SetTag("resourceKey", resourceKey);

            var resource = resourceKey.Resource;
            var name = resourceKey.Name;
            var parents = resourceKey.Parents;

            // Get resource names configured for extraction from the parent JSON context.
            // Let's say we have the following configuration:
            // workspaces:
            //   - workspace1
            //   - workspace2:
            //       apis:
            //         - api1: []
            //         - api2:
            //             diagnostics:
            //               - diagnostic1
            //               - diagnostic2
            //         - api3
            // The resource names for workspaces would be Some["workspace1", "workspace2"].
            // For workspace api diagnostics:
            // - If the parent chain is workspace2/api1, the diagnostic resource names would be Some[].
            // - If the parent chain is workspace2/api2, the diagnostic resource names would be Some["diagnostic1", "diagnostic2"].
            // - If the parent chain is workspace2/api3, the diagnostic resource names would be None.
            var result = from parentsJson in await getParentsJsonObject(parents, cancellationToken)
                                 from resourceNodes in parentsJson.GetJsonArrayProperty(resource.ConfigurationKey).ToOption()
                         let names = resourceNodes.Choose(getResourceName)
                         select names.Contains(name)
                            // For APIs, include all revisions if the root API name is in configuration
                            || (resource is ApiResource && names.Contains(ApiRevisionModule.GetRootName(name)))
                            // For workspace APIs, include all revisions if the root API name is in configuration
                            || (resource is WorkspaceApiResource && names.Contains(ApiRevisionModule.GetRootName(name)));

            activity?.SetTag("result", result.ToString());

            return result;
        };

        async ValueTask<Option<JsonObject>> getParentsJsonObject(ParentChain parents, CancellationToken cancellationToken)
        {
            var configurationJson = await configurationJsonCache.WithCancellation(cancellationToken);

            // Normalize the parent chain such that API and workspace APIs use the root API name.
            // We want `my-api`, `my-api;rev=1`, and `my-api;rev=2` to all use the name `my-api`.
            var normalizedParents = ParentChain.From(from parent in parents
                                                     let normalizedName =
                                                        parent.Resource is ApiResource or WorkspaceApiResource
                                                        ? ApiRevisionModule.GetRootName(parent.Name)
                                                        : parent.Name
                                                     select (parent.Resource, normalizedName));

            // Get the parent chain JSON from the cache
            return parentsJsonCache.GetOrAdd(normalizedParents, _ =>
                // Parent chain was not in cache, compute it.
                normalizedParents.Aggregate((Parents: ParentChain.Empty, Json: Option.Some(configurationJson)),
                                            (accumulate, item) =>
                                            {
                                                var (currentParents, currentJson) = accumulate;
                                                var (resource, name) = item;

                                                // Add the next parent to the chain
                                                var nextParents = currentParents.Append(resource, name);

                                                // Compute the JSON object next parent chain
                                                var computed = from sectionJson in currentJson
                                                               from parentJson in getParentJsonObject(resource, name, sectionJson)
                                                               select parentJson;

                                                // Cache intermediate parent JSON objects to speed up future lookups.
                                                // If A/B was previously computed, we can reuse it when computing A/B/C.
                                                // Note that on the last iteration (when nextParents == normalizedParents),
                                                // we don't cache again; the outer GetOrAdd call handles this.
                                                var nextJson = nextParents == normalizedParents
                                                                ? computed
                                                                : parentsJsonCache.GetOrAdd(nextParents, _ => computed);

                                                return (nextParents, nextJson);
                                            }).Json);
        }

        // Navigates to a specific resource's configuration within the JSON hierarchy
        // We'll use this configuration to document the method:
        // workspaces:
        //   - workspace1
        //   - workspace2:
        //       apis:
        //         - api1
        //         - api2
        static Option<JsonObject> getParentJsonObject(IResource resource, ResourceName resourceName, JsonObject jsonObject) =>
            // Using our configuration example, for workspaces, we'd get Some[JsonArray]. For products, we'd get None.
            from resourceJson in jsonObject.GetJsonArrayProperty(resource.ConfigurationKey).ToOption()
            let parents = resourceJson.Choose(node => // For workspace1, we'd get None (it's not a JSON object). For workspace2, we'd get Some[JsonObject].
                                                      from parentJsonObject in node.AsJsonObject().ToOption()
                                                          // Find the specific parent configuration by name
                                                      from parent in parentJsonObject.GetJsonObjectProperty(resourceName.ToString())
                                                                                     .ToOption()
                                                      select parent)
            from parent in parents.SingleOrNone()
            select parent;

        // Extracts resource names from JSON nodes, handling both string values and object keys
        // Let's say we have the following configuration:
        // workspaces:
        //   - workspace1
        //   - workspace2:
        //       apis:
        //         - api1
        //         - api2
        // The branch `JsonValue jsonValue` would handle workspace1.
        // The branch `JsonObject jsonObject` would handle workspace2.
        static Option<ResourceName> getResourceName(JsonNode? node)
        {
            var option = node switch
            {
                JsonValue jsonValue => jsonValue.AsString().ToOption(),
                JsonObject jsonObject => from kvp in jsonObject.Head()
                                         select kvp.Key,
                _ => Option.None
            };

            return option.Bind(name => ResourceName.From(name).ToOption());
        }
    }
}
