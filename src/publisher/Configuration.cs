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

internal delegate ValueTask<Option<JsonObject>> GetConfigurationOverride(ResourceKey resourceKey, CancellationToken cancellationToken);

internal static class ConfigurationModule
{
    public static void ConfigureGetConfigurationOverride(IHostApplicationBuilder builder)
    {
        builder.TryAddSingleton(ResolveGetConfigurationOverride);
    }

    internal static GetConfigurationOverride ResolveGetConfigurationOverride(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var configurationJsonCache = new AsyncLazy<JsonObject>(async cancellationToken => await common.ConfigurationModule.GetJsonObject(configuration, cancellationToken));
        var parentsJsonCache = new ConcurrentDictionary<ParentChain, Option<JsonObject>>();

        return async (resourceKey, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("get.configuration.override")
                                              ?.SetTag("resourceKey", resourceKey);

            var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

            var resourceJsonOption = from parentsJson in await getParentsJson(parents, cancellationToken)
                                     from resourceJsonObject in getResourceJsonObject(resource, name, parentsJson)
                                     select resourceJsonObject;

            activity?.SetTag("override.exists", resourceJsonOption.IsSome);
            resourceJsonOption.Iter(_ => logger.LogInformation("Found override for {ResourceKey}...", resourceKey));

            return resourceJsonOption;
        };

        async ValueTask<Option<JsonObject>> getParentsJson(ParentChain parents, CancellationToken cancellationToken)
        {
            var configurationJson = await configurationJsonCache.WithCancellation(cancellationToken);

            // Two-level caching strategy:
            // 1. Check if we already have the JSON object for this complete parent chain.
            return parentsJsonCache.GetOrAdd(parents, _ =>
                // 2. If not cached, traverse the parent chain incrementally, caching each step.
                // This builds up the cache progressively so partial parent paths can be reused.
                parents.Aggregate((Parents: ParentChain.Empty, Json: Option.Some(configurationJson)),
                                  (accumulate, item) =>
                                  {
                                      var (currentParents, currentJson) = accumulate;
                                      var (resource, name) = item;

                                      // Build the parent path incrementally (e.g., A -> A/B -> A/B/C)
                                      var itemAsParent = currentParents.Append(resource, name);

                                      // Second-level cache: store intermediate parent paths to avoid redundant traversal
                                      // If A/B was previously computed, we can reuse it when computing A/B/C
                                      var itemJson = parentsJsonCache.GetOrAdd(itemAsParent,
                                                                                _ => from sectionJson in currentJson
                                                                                     from parentJson in getResourceJsonObject(resource, name, sectionJson)
                                                                                     select parentJson);
                                      return (itemAsParent, itemJson);
                                  }).Json);
        }

        static Option<JsonObject> getResourceJsonObject(IResource resource, ResourceName resourceName, JsonObject sectionJson) =>
            from array in sectionJson.GetJsonArrayProperty(resource.ConfigurationKey).ToOption()
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
