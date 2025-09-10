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
internal delegate ValueTask<Option<bool>> ResourceIsInConfiguration(IResource resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken);

internal static class ConfigurationModule
{
    public static void ConfigureResourceIsInConfiguration(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(GetResourceIsInConfiguration);

    private static ResourceIsInConfiguration GetResourceIsInConfiguration(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var configurationJsonCache = new AsyncLazy<JsonObject>(async cancellationToken => await common.ConfigurationModule.GetJsonObject(configuration, cancellationToken));
        var ancestorsJsonCache = new ConcurrentDictionary<ResourceAncestors, Option<JsonObject>>();

        return async (resource, name, ancestors, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("resource.is.in.configuration")
                                              ?.SetTag("resource", resource.SingularName)
                                              ?.SetTag("name", name.ToString())
                                              ?.TagAncestors(ancestors);

            // Get resource names configured for extraction from the ancestor JSON context.
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
            // - If the ancestor chain is workspace2/api1, the diagnostic resource names would be Some[].
            // - If the ancestor chain is workspace2/api2, the diagnostic resource names would be Some["diagnostic1", "diagnostic2"].
            // - If the ancestor chain is workspace2/api3, the diagnostic resource names would be None.
            var result = from ancestorsJson in await getAncestorsJsonObject(ancestors, cancellationToken)
                         from resourceNodes in ancestorsJson.GetJsonArrayProperty(resource.PluralName).ToOption()
                         let names = resourceNodes.Choose(getResourceName)
                         select names.Contains(name)
                                // For APIs, include all revisions if the root API name is in configuration
                                || resource is ApiResource && names.Contains(ApiRevisionModule.GetRootName(name));

            activity?.SetTag("result", result.ToString());

            return result;
        };

        async ValueTask<Option<JsonObject>> getAncestorsJsonObject(ResourceAncestors ancestors, CancellationToken cancellationToken)
        {
            var configurationJson = await configurationJsonCache.WithCancellation(cancellationToken);

            // Two-level caching strategy:
            // 1. Check if we already have the JSON object for this complete ancestor chain.
            return ancestorsJsonCache.GetOrAdd(ancestors, _ =>
                // 2. If not cached, traverse the ancestor chain incrementally, caching each step.
                // This builds up the cache progressively so partial ancestor paths can be reused.
                ancestors.Aggregate((Ancestors: ResourceAncestors.Empty, Json: Option.Some(configurationJson)),
                                    (accumulate, item) =>
                                    {
                                        var (currentAncestors, option) = accumulate;
                                        var (resource, name) = item;

                                        // Build the ancestor path incrementally (e.g., A -> A/B -> A/B/C)
                                        var itemAsAncestor = currentAncestors.Append(resource, name);

                                        // Second-level cache: store intermediate ancestor paths to avoid redundant traversal
                                        // If A/B was previously computed, we can reuse it when computing A/B/C
                                        var itemJson = ancestorsJsonCache.GetOrAdd(itemAsAncestor,
                                                                                   _ => from sectionJson in option
                                                                                        from ancestorJson in getAncestorJsonObject(resource, name, sectionJson)
                                                                                        select ancestorJson);
                                        return (itemAsAncestor, itemJson);
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
        static Option<JsonObject> getAncestorJsonObject(IResource resource, ResourceName resourceName, JsonObject jsonObject) =>
            // Using our configuration example, for workspaces, we'd get Some[JsonArray]. For products, we'd get None.
            from resourceJson in jsonObject.GetJsonArrayProperty(resource.PluralName).ToOption()
            let ancestors = resourceJson.Choose(node => // For workspace1, we'd get None (it's not a JSON object). For workspace2, we'd get Some[JsonObject].
                                                        from ancestorJsonObject in node.AsJsonObject().ToOption()
                                                            // Find the specific ancestor configuration by name
                                                        from ancestor in ancestorJsonObject.GetJsonObjectProperty(resourceName.ToString()).ToOption()
                                                        select ancestor)
            from ancestor in ancestors.SingleOrNone()
            select ancestor;

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
