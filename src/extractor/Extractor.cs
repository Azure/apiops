using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask RunExtractor(CancellationToken cancellationToken);
internal delegate ValueTask<bool> ShouldExtract(ResourceKey resourceKey, Option<JsonObject> dtoOption, CancellationToken cancellationToken);
internal delegate ValueTask WriteResource(ResourceKey resourceKey, Option<JsonObject> dtoOption, CancellationToken cancellationToken);

internal static class ExtractorModule
{
    public static void ConfigureRunExtractor(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);
        ResourceModule.ConfigureListResourceNamesFromApim(builder);
        ResourceModule.ConfigureListResourceDtosFromApim(builder);
        ConfigureShouldExtract(builder);
        ConfigureWriteResource(builder);

        builder.TryAddSingleton(ResolveRunExtractor);
    }

    internal static RunExtractor ResolveRunExtractor(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isSkuSupported = provider.GetRequiredService<IsResourceSupportedInApim>();
        var listResourceNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var listResourceDtos = provider.GetRequiredService<ListResourceDtosFromApim>();
        var shouldExtract = provider.GetRequiredService<ShouldExtract>();
        var writeResource = provider.GetRequiredService<WriteResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity("run.extractor");

            logger.LogInformation("Running extractor...");

            await graph.ListTraversalRootResources()
                       .IterTaskParallel(async resource => await processResource(resource, ParentChain.Empty, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);

            logger.LogInformation("Extractor completed successfully.");
        };

        async ValueTask processResource(IResource resource, ParentChain parents, CancellationToken cancellationToken)
        {
            if (await isSkuSupported(resource, cancellationToken) is false)
            {
                logger.LogWarning("Skipping {Resource} as they are not supported in the APIM SKU.", resource.PluralName);
                return;
            }

            await listNamesAndDtos(resource, parents, cancellationToken)
                    .IterTaskParallel(async x => await extractResource(resource, x.Name, x.Dto, parents, cancellationToken),
                                      maxDegreeOfParallelism: Option.None,
                                      cancellationToken);
        }

        IAsyncEnumerable<(ResourceName Name, Option<JsonObject> Dto)> listNamesAndDtos(IResource resource, ParentChain parents, CancellationToken cancellationToken) =>
            resource switch
            {
                IResourceWithDto resourceWithDto =>
                    from x in listResourceDtos(resourceWithDto, parents, cancellationToken)
                    select (x.Name, Option.Some(x.Dto)),
                _ =>
                    from name in listResourceNames(resource, parents, cancellationToken)
                    select (name, Option<JsonObject>.None())
            };

        async ValueTask extractResource(IResource resource, ResourceName name, Option<JsonObject> dtoOption, ParentChain parents, CancellationToken cancellationToken)
        {
            var resourceKey = ResourceKey.From(resource, name, parents);

            // Skip the resource if it should not be extracted.
            if (await shouldExtract(resourceKey, dtoOption, cancellationToken) is false)
            {
                return;
            }

            // Write the resource's artifacts
            await writeResource(resourceKey, dtoOption, cancellationToken);

            // Process the resource's successors
            var successorParents = parents.Append(resource, name);
            await graph.ListTraversalSuccessors(resource)
                       .Where(successor => shouldExtractSuccessor(successor, resourceKey))
                       .IterTaskParallel(async successor => await processResource(successor, successorParents, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);
        }

        static bool shouldExtractSuccessor(IResource successor, ResourceKey parent) =>
            parent.Resource switch
            {
                // API releases are only extracted for root APIs
                ApiResource => successor is not ApiReleaseResource || ApiRevisionModule.IsRootName(parent.Name),
                WorkspaceApiResource => successor is not WorkspaceApiReleaseResource || ApiRevisionModule.IsRootName(parent.Name),
                _ => true
            };
    }

    private static void ConfigureShouldExtract(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureResourceIsInConfiguration(builder);

        builder.TryAddSingleton(ResolveShouldExtract);
    }

    internal static ShouldExtract ResolveShouldExtract(IServiceProvider provider)
    {
        var resourceIsInConfiguration = provider.GetRequiredService<ResourceIsInConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resourceKey, dtoOption, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("should.extract")
                                              ?.SetTag("resourceKey", resourceKey);

            var result = await shouldExtract(resourceKey, dtoOption, cancellationToken);

            activity?.SetTag("result", result);

            return result;
        };

        async ValueTask<bool> shouldExtract(ResourceKey resourceKey, Option<JsonObject> dtoOption, CancellationToken cancellationToken)
        {
            var (resource, name) = (resourceKey.Resource, resourceKey.Name);

            // Never extract the `master` subscription
            if (resource is SubscriptionResource && name == SubscriptionResource.Master)
            {
                logger.LogWarning("Skipping master subscription '{Name}'...", name);
                return false;
            }

            // Never extract system groups            
            if (resource is GroupResource
                && (name == GroupResource.Administrators || name == GroupResource.Developers || name == GroupResource.Guests))
            {
                logger.LogWarning("Skipping system group '{Name}'...", name);
                return false;
            }

            // Never extract workspace system groups
            if (resource is WorkspaceGroupResource
                && (name == WorkspaceGroupResource.Administrators || name == WorkspaceGroupResource.Developers || name == WorkspaceGroupResource.Guests))
            {
                logger.LogWarning("Skipping workspace system group '{Name}'...", name);
                return false;
            }

            // Never extract composite APIs that are not the current revision. Publisher round-tripping doesn't work properly.
            if (resource is ICompositeResource compositeResource
                && compositeResource.Secondary is ApiResource or WorkspaceApiResource)
            {
                var apiNameOption = compositeResource switch
                {
                    ILinkResource linkResource => from dto in dtoOption
                                                  from apiName in linkResource.GetSecondaryResourceName(dto)
                                                  select apiName,
                    _ => name
                };

                var nonCurrentRevisionName = apiNameOption.Where(apiName => ApiRevisionModule.IsRootName(apiName) is false)
                                                          .IfNoneNull();

                if (nonCurrentRevisionName is not null)
                {
                    logger.LogWarning("Skipping composite resource '{ResourceKey}'. API '{ResourceName}' is not the current API revision.", resourceKey, nonCurrentRevisionName);
                    return false;
                }
            }

            // Skip resources based on configuration
            var isInConfigurationOption = await resourceIsInConfiguration(resourceKey, cancellationToken);
            var isInConfiguration = isInConfigurationOption.IfNone(() => true);
            if (isInConfiguration is false)
            {
                logger.LogWarning("Skipping {ResourceKey} as it is not in configuration...", resourceKey);
                return false;
            }

            return true;
        }
    }

    private static void ConfigureWriteResource(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureWriteInformationFile(builder);
        ResourceModule.ConfigureWritePolicyFile(builder);
        ResourceModule.ConfigureGetApiSpecificationFromApim(builder);
        ResourceModule.ConfigureWriteApiSpecificationFile(builder);

        builder.TryAddSingleton(ResolveWriteResource);
    }

    internal static WriteResource ResolveWriteResource(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteInformationFile>();
        var writePolicyFile = provider.GetRequiredService<WritePolicyFile>();
        var getApiSpecification = provider.GetRequiredService<GetApiSpecificationFromApim>();
        var writeApiSpecificationFile = provider.GetRequiredService<WriteApiSpecificationFile>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resourceKey, dtoOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("write.resource")
                                       ?.SetTag("resourceKey", resourceKey);

            await dtoOption.IterTask(async dto =>
            {
                var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

                if (resource is IResourceWithInformationFile resourceWithInformationFile)
                {
                    logger.LogInformation("Writing information file for {ResourceKey}...", resourceKey);
                    await writeInformationFile(resourceWithInformationFile, name, dto, parents, cancellationToken);
                }

                if (resource is IPolicyResource policyResource)
                {
                    logger.LogInformation("Writing policy file for {ResourceKey}...", resourceKey);
                    await writePolicyFile(policyResource, name, dto, parents, cancellationToken);
                }

                if (resource is ApiResource)
                {
                    await writeSpecification(resourceKey, dto, cancellationToken);
                }

                if (resource is WorkspaceApiResource)
                {
                    await writeSpecification(resourceKey, dto, cancellationToken);
                }
            });
        };

        async ValueTask writeSpecification(ResourceKey resourceKey, JsonObject dto, CancellationToken cancellationToken)
        {
            var option = await getApiSpecification(resourceKey, dto, cancellationToken);

            await option.IterTask(async tuple =>
            {
                var (specification, contents) = tuple;

                // APIM exports invalid WSDL that cannot be re-imported. Skip writing.
                if (specification is ApiSpecification.Wsdl)
                {
                    logger.LogWarning("Skipping SOAP specification file for {ResourceKey}. APIM exports invalid WSDL that cannot be reimported.", resourceKey);
                    return;
                }

                logger.LogInformation("Writing specification file for {ResourceKey}...", resourceKey);
                await writeApiSpecificationFile(resourceKey, specification, contents, cancellationToken);
            });
        }
    }
}