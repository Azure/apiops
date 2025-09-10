using Azure.Core.Pipeline;
using common;
using integration.tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.System.Text.Json;

namespace integration.tests;

internal delegate ValueTask RunPublisher(PublisherOptions options, CancellationToken cancellationToken);
internal delegate ValueTask ValidatePublisherWithoutCommit(ResourceModels models, Option<JsonObject> overrides, CancellationToken cancellationToken);
internal delegate ValueTask ValidatePublisherWithCommit(ResourceModels models, Option<JsonObject> overrides, Option<ResourceModels> previousModels, CancellationToken cancellationToken);

internal sealed record PublisherOptions
{
    public required ServiceDirectory ServiceDirectory { get; init; }
    public Option<JsonObject> JsonOverrides { get; init; } = Option.None;
    public Option<CommitId> CommitId { get; init; } = Option.None;
}

internal static class PublisherModule
{
    public static void ConfigureRunPublisher(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureBuilder(builder);
        builder.TryAddSingleton(GetRunPublisher);
    }

    private static RunPublisher GetRunPublisher(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (options, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("run.publisher");

            await writeConfigurationYaml(options, cancellationToken);

            var arguments = getProgramArguments(options);
            await publisher.Program.Main([.. arguments]);
        };

        async ValueTask writeConfigurationYaml(PublisherOptions options, CancellationToken cancellationToken) =>
            await options.JsonOverrides
                         .IterTask(async json =>
                         {
                             var file = getConfigurationFile(options);
                             var yaml = YamlConverter.Serialize(json);
                             var content = BinaryData.FromString(yaml);
                             await file.OverwriteWithBinaryData(content, cancellationToken);
                         });

        FileInfo getConfigurationFile(PublisherOptions options) =>
            options.ServiceDirectory
                   .ToDirectoryInfo()
                   .GetChildFile("configuration.publisher.yaml");

        ImmutableArray<string> getProgramArguments(PublisherOptions options)
        {
            var arguments = new List<string>();

            arguments.AddRange(["--API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH", options.ServiceDirectory.ToDirectoryInfo().FullName]);
            options.JsonOverrides.Iter(_ => arguments.AddRange([$"--{ConfigurationModule.YamlPath}", getConfigurationFile(options).FullName]));
            options.CommitId.Iter(commitId => arguments.AddRange(["--COMMIT_ID", commitId.ToString()]));

            return [.. arguments];
        }
    }

    public static void ConfigureValidatePublisherWithoutCommit(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ResourceGraphModule.ConfigureBuilder(builder);
        ServiceModule.ConfigureIsSkuSupported(builder);
        ServiceModule.ConfigureShouldSkipResource(builder);

        builder.TryAddSingleton(GetValidatePublisherWithoutCommit);
    }

    private static ValidatePublisherWithoutCommit GetValidatePublisherWithoutCommit(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isSkuSupported = provider.GetRequiredService<IsSkuSupported>();
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (models, overrides, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("validate.publisher.without.commit");

            await validateModelsWerePublished(models, overrides, cancellationToken);
            await validateAllApimResourcesAreInModels(models, cancellationToken);
        };

        async ValueTask validateModelsWerePublished(ResourceModels models, Option<JsonObject> overrides, CancellationToken cancellationToken) =>
            await models.SelectMany(kvp => kvp.Value)
                        .IterTaskParallel(async node =>
                        {
                            var resource = node.Model.AssociatedResource;
                            var ancestors = node.GetResourceAncestors();
                            var name = node.Model.Name;

                            if (await shouldSkipResource(resource, name, ancestors, cancellationToken))
                            {
                                return;
                            }

                            await ValidateApimMatchesNode(node, overrides, serviceUri, pipeline, cancellationToken);
                        }, maxDegreeOfParallelism: Option.None, cancellationToken);

        async ValueTask validateAllApimResourcesAreInModels(ResourceModels models, CancellationToken cancellationToken) =>
            await graph.GetTraversalRootResources()
                       .IterTaskParallel(async resource => await validateResourceNamesAreInModels(resource, ResourceAncestors.Empty, models, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);

        async ValueTask validateResourceNamesAreInModels(IResource resource, ResourceAncestors ancestors, ResourceModels models, CancellationToken cancellationToken)
        {
            if (await isSkuSupported(resource, ancestors, cancellationToken) is false)
            {
                return;
            }

            await resource.ListNames(ancestors, serviceUri, pipeline, cancellationToken)
                          .IterTaskParallel(async name => await validateResourceNameIsInModels(resource, name, ancestors, models, cancellationToken),
                                            maxDegreeOfParallelism: Option.None,
                                            cancellationToken);
        }

        async ValueTask validateResourceNameIsInModels(IResource resource, ResourceName name, ResourceAncestors ancestors, ResourceModels models, CancellationToken cancellationToken)
        {
            if (await shouldSkipResource(resource, name, ancestors, cancellationToken))
            {
                return;
            }

            // Ensure the APIM resource is in the models
            var exception = new InvalidOperationException($"Resource {resource.SingularName} {name}{ancestors.ToLogString()} is not present in the models.");

            switch (resource)
            {
                // For APIs, we only validate that the root name is in the models, regardless of the revisions
                case ApiResource:
                    var rootName = ApiRevisionModule.GetRootName(name);

                    models.Find(resource)
                          .Where(apis => apis.Any(node => ApiRevisionModule.GetRootName(node.Model.Name) == rootName && ancestors == node.GetResourceAncestors()))
                          .IfNone(() => throw exception);

                    break;
                default:
                    models.Find(resource, name, ancestors)
                          .IfNone(() => throw exception);

                    break;
            }

            // Check the resource's successors
            var successorAncestors = ancestors.Append(resource, name);

            await graph.GetTraversalSuccessors(resource)
                       .IterTaskParallel(async successor => await validateResourceNamesAreInModels(successor, successorAncestors, models, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);
        }
    }

    private static async ValueTask ValidateApimMatchesNode(ModelNode node, Option<JsonObject> overrides, ServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var model = node.Model;
        var resource = model.AssociatedResource;

        if (resource is not IResourceWithDto resourceWithDto)
        {
            return;
        }

        if (model is not IDtoTestModel dtoTestModel)
        {
            throw new InvalidOperationException($"Model for resource {resource} must be DTO test model.");
        }

        var name = model.Name;
        var ancestors = node.GetResourceAncestors();
        var apimContentsResult = await getApimContents(resourceWithDto, name, ancestors);

        // Secret named values might not get published if they don't have a value
        if (resource is NamedValueResource
            && dtoTestModel is NamedValueModel namedValueModel
            && namedValueModel.IsSecret
            && apimContentsResult.IsError)
        {
            return;
        }

        var apimContents = apimContentsResult.IfErrorThrow();
        var overrideJson = getOverride(resource, name, ancestors);
        var dtosMatch = dtoTestModel.MatchesDto(apimContents, overrideJson);
        if (dtosMatch is false)
        {
            throw new InvalidOperationException($"DTO for {resource.SingularName} {name}{ancestors.ToLogString()} does not match the expected DTO.");
        }

        async ValueTask<Result<JsonObject>> getApimContents(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors) =>
            resource switch
            {
                ICompositeResource and not ILinkResource => new JsonObject(),
                _ => await resource.GetDto(name, ancestors, serviceUri, pipeline, cancellationToken)
            };

        Option<JsonObject> getOverride(IResource resource, ResourceName name, ResourceAncestors ancestors) =>
            ancestors.Aggregate(overrides,
                                (option, ancestor) => from section in option
                                                      from resourceJson in getResourceJson(ancestor.Resource, ancestor.Name, section)
                                                      select resourceJson)
                     .Bind(json => getResourceJson(resource, name, json));

        static Option<JsonObject> getResourceJson(IResource resource, ResourceName name, JsonObject json) =>
            from resources in json.GetJsonArrayProperty(resource.PluralName).ToOption()
            from resourceJson in resources.Pick(node => from jsonObject in node.AsJsonObject().ToOption()
                                                        from resourceName in jsonObject.GetStringProperty("name").ToOption()
                                                        where resourceName == name.ToString()
                                                        select jsonObject)
            select resourceJson;
    }

    public static void ConfigureValidatePublisherWithCommit(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ServiceModule.ConfigureShouldSkipResource(builder);

        builder.TryAddSingleton(GetValidatePublisherWithCommit);
    }

    private static ValidatePublisherWithCommit GetValidatePublisherWithCommit(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (models, overrides, previousModelsOption, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("validate.publisher.with.commit");

            var added = ModelNodeSet.Empty;
            var updated = ModelNodeSet.Empty;
            var unchanged = ModelNodeSet.Empty;
            var deleted = ModelNodeSet.Empty;

            var previousModels = previousModelsOption.IfNone(() => ResourceModels.Empty);

            foreach (var (resource, set) in models)
            {
                var previousSet = previousModels.Find(resource)
                                                .IfNone(() => ModelNodeSet.Empty);

                foreach (var node in set)
                {
                    var name = node.Model.Name;

                    previousSet.Find(name, node.Predecessors)
                               .Match(// Node exists in the previous set
                                      previousNode =>
                                      {
                                          if (previousNode.Model.Equals(node.Model))
                                          {
                                              // Node is unchanged
                                              unchanged = unchanged.Add(node);
                                          }
                                          else
                                          {
                                              // Node has been updated
                                              updated = updated.Add(node);
                                          }
                                      },
                                      // Model does not exist in the previous set
                                      () => added = added.Add(node));
                }
            }

            foreach (var (resource, previousSet) in previousModels)
            {
                var currentSet = models.Find(resource)
                                       .IfNone(() => ModelNodeSet.Empty);

                foreach (var node in previousSet)
                {
                    var name = node.Model.Name;

                    currentSet.Find(name, node.Predecessors)
                              .Match(// Node exists in the current set
                                     _ => { },
                                     // Node does not exist in the current set
                                     () =>
                                     {
                                         // Skip "deleted" API revisions that have been made current
                                         if (resource is ApiResource
                                             && ApiRevisionModule.Parse(name)
                                                                 .Map(x => currentSet.Any(currentNode => currentNode.Model.Name == x.RootName
                                                                                                         && currentNode.Model is ApiModel apiModel
                                                                                                         && apiModel.RevisionNumber == x.Revision))
                                                                 .IsSome)
                                         {
                                             return;
                                         }

                                         deleted = deleted.Add(node);
                                     });
                }
            }

            // Validate new models match APIM
            await validateApimMatchesSet(added, overrides, cancellationToken);

            // Validate updated models match APIM
            await validateApimMatchesSet(updated, overrides, cancellationToken);

            // Validate unchanged models match APIM
            await validateApimMatchesSet(unchanged, overrides: Option.None, cancellationToken);

            // Validate deleted models are not present in APIM
            await deleted.IterTaskParallel(async node =>
            {
                var resource = node.Model.AssociatedResource;
                var name = node.Model.Name;
                var ancestors = node.GetResourceAncestors();

                if (await shouldSkipResource(resource, name, ancestors, cancellationToken))
                {
                    return;
                }

                if (await resource.Exists(name, ancestors, serviceUri, pipeline, cancellationToken))
                {
                    throw new InvalidOperationException($"Expected {resource.SingularName} {name}{ancestors.ToLogString()} to have been deleted from APIM.");
                }
            }, maxDegreeOfParallelism: Option.None, cancellationToken);
        };

        async ValueTask validateApimMatchesSet(ModelNodeSet nodes, Option<JsonObject> overrides, CancellationToken cancellationToken) =>
            await nodes.IterTaskParallel(async node =>
            {
                var resource = node.Model.AssociatedResource;
                var ancestors = node.GetResourceAncestors();
                var name = node.Model.Name;

                if (await shouldSkipResource(resource, name, ancestors, cancellationToken))
                {
                    return;
                }

                await ValidateApimMatchesNode(node, overrides, serviceUri, pipeline, cancellationToken);
            }, maxDegreeOfParallelism: Option.None, cancellationToken);
    }
}