using common;
using DotNext.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<JsonObject>> GetConfigurationOverride(IResource resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken);

internal static class ConfigurationModule
{
    public static void ConfigureGetConfigurationOverride(IHostApplicationBuilder builder)
    {
        builder.TryAddSingleton(GetGetConfigurationOverride);
    }

    private static GetConfigurationOverride GetGetConfigurationOverride(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var configurationJsonCache = new AsyncLazy<JsonObject>(async cancellationToken => await common.ConfigurationModule.GetJsonObject(configuration, cancellationToken));
        var ancestorsJsonCache = new ConcurrentDictionary<ResourceAncestors, Option<JsonObject>>();

        return async (resource, name, ancestors, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("get.configuration.override")
                                              ?.SetTag("resource", resource.SingularName)
                                              ?.SetTag("name", name.ToString())
                                              ?.TagAncestors(ancestors);

            var resourceJsonOption = from ancestorsJson in await getAncestorsJson(ancestors, cancellationToken)
                                     from resourceJsonObject in getResourceJsonObject(resource, name, ancestorsJson)
                                     select resourceJsonObject;

            activity?.SetTag("override.exists", resourceJsonOption.IsSome);
            resourceJsonOption.Iter(_ => logger.LogInformation("Found override for {Resource} '{Name}'{Ancestors}...", resource.SingularName, name, ancestors.ToLogString()));

            return resourceJsonOption;
        };

        async ValueTask<Option<JsonObject>> getAncestorsJson(ResourceAncestors ancestors, CancellationToken cancellationToken)
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
                                        var (currentAncestors, currentJson) = accumulate;
                                        var (resource, name) = item;

                                        // Build the ancestor path incrementally (e.g., A -> A/B -> A/B/C)
                                        var itemAsAncestor = currentAncestors.Append(resource, name);

                                        // Second-level cache: store intermediate ancestor paths to avoid redundant traversal
                                        // If A/B was previously computed, we can reuse it when computing A/B/C
                                        var itemJson = ancestorsJsonCache.GetOrAdd(itemAsAncestor,
                                                                                   _ => from sectionJson in currentJson
                                                                                        from ancestorJson in getResourceJsonObject(resource, name, sectionJson)
                                                                                        select ancestorJson);
                                        return (itemAsAncestor, itemJson);
                                    }).Json);
        }

        static Option<JsonObject> getResourceJsonObject(IResource resource, ResourceName resourceName, JsonObject sectionJson) =>
            from array in sectionJson.GetJsonArrayProperty(resource.PluralName).ToOption()
            let resourceJsonObjects = array.Choose(resourceNode => from resourceJsonObject in resourceNode.AsJsonObject().ToOption()
                                                                   from nameString in resourceJsonObject.GetStringProperty("name").ToOption()
                                                                   where nameString == resourceName.ToString()
                                                                   select resourceJsonObject)
            from resourceJsonObject in resourceJsonObjects.SingleOrNone()
            select resource switch
            {
                // For APIs, remove properties that should not be overridden
                ApiResource => resourceJsonObject.SetProperty("properties", resourceJsonObject.GetJsonObjectProperty("properties")
                                                                                              .IfError(_ => [])
                                                                                              .RemoveProperty("apiRevision")
                                                                                              .RemoveProperty("isCurrent")),
                _ => resourceJsonObject
            };
    }
}
