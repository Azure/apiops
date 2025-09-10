using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask RunExtractor(CancellationToken cancellationToken);
internal delegate IAsyncEnumerable<(ResourceName Name, Option<JsonObject> Dto)> ListResources(IResource resource, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask<bool> ShouldExtract(IResource resource, ResourceName name, Option<JsonObject> dtoOption, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask WriteResource(IResource resource, ResourceName name, Option<JsonObject> dtoOption, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask WriteInformationFile(IResourceWithInformationFile resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask WritePolicyFile(IPolicyResource resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, CancellationToken cancellationToken);

internal static class ExtractorModule
{
    public static void ConfigureRunExtractor(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureBuilder(builder);
        ConfigureListResources(builder);
        ConfigureShouldExtract(builder);
        ConfigureWriteResource(builder);

        builder.TryAddSingleton(GetRunExtractor);
    }

    private static RunExtractor GetRunExtractor(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var listResources = provider.GetRequiredService<ListResources>();
        var shouldExtract = provider.GetRequiredService<ShouldExtract>();
        var writeResource = provider.GetRequiredService<WriteResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("run.extractor");

            logger.LogInformation("Running extractor...");

            await graph.GetTraversalRootResources()
                       .IterTaskParallel(async resource => await processResource(resource, ResourceAncestors.Empty, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);

            logger.LogInformation("Extractor completed successfully.");
        };

        async ValueTask processResource(IResource resource, ResourceAncestors ancestors, CancellationToken cancellationToken) =>
            await listResources(resource, ancestors, cancellationToken)
                    .IterTaskParallel(async x => await extractResource(resource, x.Name, x.Dto, ancestors, cancellationToken),
                                      maxDegreeOfParallelism: Option.None,
                                      cancellationToken);

        async ValueTask extractResource(IResource resource, ResourceName name, Option<JsonObject> dtoOption, ResourceAncestors ancestors, CancellationToken cancellationToken)
        {
            // Skip the resource if it should not be extracted.
            var extract = await shouldExtract(resource, name, dtoOption, ancestors, cancellationToken);

            if (extract is false)
            {
                return;
            }

            // Extract the resource
            await writeResource(resource, name, dtoOption, ancestors, cancellationToken);

            // Process the resource's successors
            var successorAncestors = ancestors.Append(resource, name);

            await graph.GetTraversalSuccessors(resource)
                       .IterTaskParallel(async successor => await processResource(successor, successorAncestors, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);
        }
    }

    private static void ConfigureListResources(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(GetListResources);
    }

    private static ListResources GetListResources(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return (resource, ancestors, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("list.resources")
                                       ?.SetTag("resource", resource.SingularName)
                                       ?.TagAncestors(ancestors);

            return list(resource, ancestors, cancellationToken);
        };

        async IAsyncEnumerable<(ResourceName, Option<JsonObject>)> list(IResource resource, ResourceAncestors ancestors, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Skip unsupported SKUs
            if (await resource.IsSkuSupported(ancestors, serviceUri, pipeline, cancellationToken) is false)
            {
                yield break;
            }

            switch (resource)
            {
                // For resources with DTOs, list names and DTOs
                case IResourceWithDto resourceWithDto:
                    var items = resourceWithDto.ListNamesAndDtos(ancestors, serviceUri, pipeline, cancellationToken);

                    await foreach (var (name, dto) in items.WithCancellation(cancellationToken))
                    {
                        yield return (name, Option.Some(dto));
                    }

                    break;

                // Otherwise, list names and no DTOs
                default:
                    var names = resource.ListNames(ancestors, serviceUri, pipeline, cancellationToken);

                    await foreach (var name in names.WithCancellation(cancellationToken))
                    {
                        yield return (name, Option.None);
                    }

                    break;
            }
        }
    }

    private static void ConfigureShouldExtract(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureResourceIsInConfiguration(builder);

        builder.TryAddSingleton(GetShouldExtract);
    }

    private static ShouldExtract GetShouldExtract(IServiceProvider provider)
    {
        var resourceIsInConfiguration = provider.GetRequiredService<ResourceIsInConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resource, name, dtoOption, ancestors, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("should.extract")
                                              ?.SetTag("resource", resource.SingularName)
                                              ?.SetTag("name", name.ToString())
                                              ?.TagAncestors(ancestors);

            var extract = await shouldExtract(resource, name, ancestors, cancellationToken);

            activity?.SetTag("extract", extract);

            return extract;
        };

        async ValueTask<bool> shouldExtract(IResource resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken)
        {
            // Never extract the `master` subscription
            if (resource is SubscriptionResource && name == SubscriptionResource.Master)
            {
                logger.LogWarning("Skipping master subscription '{Name}'...", name);
                return false;
            }
            // Never extract system groups
            else if (resource is GroupResource
                     && (name == GroupResource.Administrators || name == GroupResource.Developers || name == GroupResource.Guests))
            {
                logger.LogWarning("Skipping system group '{Name}'...", name);
                return false;
            }
            // Check from configuration. If no configuration was defined for the resource type, extract all.
            else
            {
                var option = await resourceIsInConfiguration(resource, name, ancestors, cancellationToken);

                option.Where(result => result is false)
                      .Iter(_ => logger.LogWarning("Skipping {Resource} '{Name}'{Ancestors} as it is not in configuration.", resource.SingularName, name, ancestors.ToLogString()));

                return option.IfNone(() => true);
            }
        }
    }

    private static void ConfigureWriteResource(IHostApplicationBuilder builder)
    {
        ConfigureWriteInformationFile(builder);
        ConfigureWritePolicyFile(builder);

        builder.TryAddSingleton(GetWriteResource);
    }

    private static WriteResource GetWriteResource(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteInformationFile>();
        var writePolicyFile = provider.GetRequiredService<WritePolicyFile>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (resource, name, dtoOption, ancestors, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("write.resource")
                                       ?.SetTag("resource", resource.SingularName)
                                       ?.SetTag("name", name.ToString())
                                       ?.TagAncestors(ancestors);

            if (resource is IResourceWithInformationFile resourceWithInformationFile)
            {
                await dtoOption.IterTask(async dto => await writeInformationFile(resourceWithInformationFile, name, dto, ancestors, cancellationToken));
            }

            if (resource is IPolicyResource policyResource)
            {
                await dtoOption.IterTask(async dto => await writePolicyFile(policyResource, name, dto, ancestors, cancellationToken));
            }
        };
    }

    private static void ConfigureWriteInformationFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetWriteInformationFile);
    }

    private static WriteInformationFile GetWriteInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resource, name, dto, ancestors, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("write.information.file")
                                              ?.SetTag("resource", resource.SingularName)
                                              ?.SetTag("name", name.ToString())
                                              ?.TagAncestors(ancestors);

            logger.LogInformation("Writing information file for {Resource} '{Name}'{Ancestors}...", resource.SingularName, name, ancestors.ToLogString());

            await resource.WriteInformationFile(name, dto, ancestors, serviceDirectory, cancellationToken);
        };
    }

    private static void ConfigureWritePolicyFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);

        builder.TryAddSingleton(GetWritePolicyFile);
    }

    private static WritePolicyFile GetWritePolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resource, name, dto, ancestors, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("write.policy.file")
                                              ?.SetTag("name", name.ToString())
                                              ?.TagAncestors(ancestors);

            logger.LogInformation("Writing policy file for {Resource} '{Name}'{Ancestors}...", resource.SingularName, name, ancestors.ToLogString());

            await resource.WritePolicyFile(name, dto, ancestors, serviceDirectory, cancellationToken);
        };
    }
}