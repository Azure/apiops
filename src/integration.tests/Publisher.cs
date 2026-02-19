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
        ResourceGraphModule.ConfigureResourceGraph(builder);

        builder.TryAddSingleton(ResolveRunPublisher);
    }

    private static RunPublisher ResolveRunPublisher(IServiceProvider provider)
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
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);
        ResourceModule.ConfigureListResourceNamesFromApim(builder);
        ResourceModule.ConfigureGetOptionalResourceDtoFromApim(builder);
        ServiceModule.ConfigureShouldSkipResource(builder);

        builder.TryAddSingleton(ResolveValidatePublisherWithoutCommit);
    }

    private static ValidatePublisherWithoutCommit ResolveValidatePublisherWithoutCommit(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isSkuSupported = provider.GetRequiredService<IsResourceSupportedInApim>();
        var listNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var getDto = provider.GetRequiredService<GetOptionalResourceDtoFromApim>();
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
                            var parents = node.GetResourceParentChain();
                            var name = node.Model.Name;

                            var resourceKey = ResourceKey.From(resource, name, parents);

                            if (await shouldSkipResource(resourceKey, cancellationToken))
                            {
                                return;
                            }

                            await ValidateApimMatchesNode(node, overrides, getDto, cancellationToken);
                        }, maxDegreeOfParallelism: Option.None, cancellationToken);

        async ValueTask validateAllApimResourcesAreInModels(ResourceModels models, CancellationToken cancellationToken) =>
            await graph.ListTraversalRootResources()
                       .IterTaskParallel(async resource => await validateResourceNamesAreInModels(resource, ParentChain.Empty, models, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);

        async ValueTask validateResourceNamesAreInModels(IResource resource, ParentChain parents, ResourceModels models, CancellationToken cancellationToken)
        {
            if (await isSkuSupported(resource, cancellationToken) is false)
            {
                return;
            }

            await listNames(resource, parents, cancellationToken)
                    .IterTaskParallel(async name => await validateResourceNameIsInModels(resource, name, parents, models, cancellationToken),
                                      maxDegreeOfParallelism: Option.None,
                                      cancellationToken);
        }

        async ValueTask validateResourceNameIsInModels(IResource resource, ResourceName name, ParentChain parents, ResourceModels models, CancellationToken cancellationToken)
        {
            var resourceKey = ResourceKey.From(resource, name, parents);

            if (await shouldSkipResource(resourceKey, cancellationToken))
            {
                return;
            }

            if (await MightBeAutomaticallyCreatedResource(resourceKey, models, getDto, cancellationToken))
            {
                return;
            }

            // Ensure the APIM resource is in the models
            var exception = new InvalidOperationException($"Resource {ResourceKey.From(resource, name, parents)} is not present in the models.");

            switch (resource)
            {
                // For APIs, we only validate that the root name is in the models, regardless of the revisions
                case ApiResource:
                    var rootName = ApiRevisionModule.GetRootName(name);

                    models.Find(resource)
                          .Where(apis => apis.Any(node => ApiRevisionModule.GetRootName(node.Model.Name) == rootName && parents == node.GetResourceParentChain()))
                          .IfNone(() => throw exception);

                    break;
                case IChildResource childResource when childResource.Parent is ApiResource:
                    // For resources that are children of an API, we validate that either the specific revision or the root API is in the models
                    models.Find(resource, name, parents)
                          .IfNone(() =>
                          {
                              var api = parents.Last();
                              var rootName = ApiRevisionModule.GetRootName(api.Name);
                              var parentsWithRootApi = ParentChain.From([.. parents.SkipLast(1)])
                                                                  .Append(api.Resource, rootName);

                              return models.Find(resource, name, parentsWithRootApi);
                          })
                          .IfNone(() => throw exception);

                    break;

                default:
                    models.Find(resource, name, parents)
                          .IfNone(() => throw exception);

                    break;
            }

            // Check the resource's successors
            var successorParents = parents.Append(resource, name);

            await graph.ListTraversalSuccessors(resource)
                       .IterTaskParallel(async successor => await validateResourceNamesAreInModels(successor, successorParents, models, cancellationToken),
                                         maxDegreeOfParallelism: Option.None,
                                         cancellationToken);
        }
    }

    /// <summary>
    /// APIM automatically creates certain resources. We want to skip validation for them.
    /// </summary>
    private static async ValueTask<bool> MightBeAutomaticallyCreatedResource(ResourceKey resourceKey, ResourceModels models, GetOptionalResourceDtoFromApim getDto, CancellationToken cancellationToken)
    {
        var (resource, name, parents) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);
        var isNotRootApiName = (ResourceName name) => ApiRevisionModule.IsRootName(name) is false;

        // If the resource exists in the models, it's probably not automatically created
        if (models.Find(resource, name, parents).IsSome)
        {
            return false;
        }

        return resource switch
        {
            ApiPolicyResource apiPolicyResource => checkApiPolicy(apiPolicyResource),
            ApiDiagnosticResource apiDiagnosticResource => checkApiDiagnostic(apiDiagnosticResource),
            ProductApiResource productApiResource => await checkProductApi(productApiResource),
            TagApiResource tagApiResource => await checkTagApi(tagApiResource),
            _ => false,
        };

        // Check if models contain a root API with this policy name
        bool checkApiPolicy(ApiPolicyResource resource)
        {
            var option = from apiName in getParentResourceName(resource)
                             // Ensure the API name is revisioned
                         let rootName = ApiRevisionModule.GetRootName(apiName)
                         where apiName != rootName
                         // Check whether models contain a link between the policy and the root API
                         select models.Choose<ApiPolicyModel>()
                                      .Any(x => x.Predecessors.Any(predecessor => predecessor.Model is ApiModel
                                                                                  && predecessor.Model.Name == rootName));

            return option.IfNone(() => false);
        }

        // Check if models contain a root API with this diagnostic name
        bool checkApiDiagnostic(ApiDiagnosticResource resource)
        {
            var option = from apiName in getParentResourceName(resource)
                             // Ensure the API name is revisioned
                         let rootName = ApiRevisionModule.GetRootName(apiName)
                         where apiName != rootName
                         // Check whether models contain a link between the diagnostic and the root API
                         select models.Choose<ApiDiagnosticModel>()
                                      .Any(x => x.Model.Name == name
                                                && x.Predecessors.Any(predecessor => predecessor.Model is ApiModel
                                                                                     && predecessor.Model.Name == rootName));

            return option.IfNone(() => false);
        }

        // Check if models contain a root API associated with this product
        async ValueTask<bool> checkProductApi(ProductApiResource resource)
        {
            var option = from apiName in await getSecondaryResourceName(resource)
                             // Ensure the API name is revisioned
                         let rootName = ApiRevisionModule.GetRootName(apiName)
                         where apiName != rootName
                         // Check whether models contain a link between the product and the root API
                         from productName in getPrimaryResourceName(resource)
                         select models.Choose<ProductApiModel>()
                                      .Any(x => x.Model.PrimaryResourceName == productName
                                                && x.Model.SecondaryResourceName == rootName);

            return option.IfNone(() => false);
        }

        Option<ResourceName> getParentResourceName(IChildResource childResource) =>
            parents.LastOrDefault() is (IResource parentResource, ResourceName parentName)
                && parentResource == childResource.Parent
                ? parentName
                : Option.None;

        Option<ResourceName> getPrimaryResourceName(ICompositeResource compositeResource) =>
            parents.LastOrDefault() is (IResource parentResource, ResourceName parentName)
                && parentResource == compositeResource.Primary
                ? parentName
                : Option.None;

        async ValueTask<Option<ResourceName>> getSecondaryResourceName(ICompositeResource compositeResource) =>
            compositeResource is ILinkResource linkResource
                ? from dtoJson in await getDto(linkResource, name, parents, cancellationToken)
                  let secondaryNameResult = from properties in dtoJson.GetJsonObjectProperty("properties")
                                            from secondaryId in properties.GetStringProperty(linkResource.DtoPropertyNameForLinkedResource)
                                            let secondaryIdString = secondaryId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                                            from secondaryName in ResourceName.From(secondaryIdString)
                                            select secondaryName
                  from secondaryName in secondaryNameResult.ToOption()
                  select secondaryName
                : name;

        async ValueTask<bool> checkTagApi(TagApiResource resource)
        {
            var option = from apiName in await getSecondaryResourceName(resource)
                             // Ensure the API name is revisioned
                         let rootName = ApiRevisionModule.GetRootName(apiName)
                         where apiName != rootName
                         // Check whether models contain a link between the tag and the root API
                         from tagName in getPrimaryResourceName(resource)
                         select models.Choose<TagApiModel>()
                                      .Any(x => x.Model.PrimaryResourceName == tagName
                                                && x.Model.SecondaryResourceName == rootName);

            return option.IfNone(() => false);
        }
    }

    private static async ValueTask ValidateApimMatchesNode(ModelNode node, Option<JsonObject> overrides, GetOptionalResourceDtoFromApim getDto, CancellationToken cancellationToken)
    {
        var model = node.Model;
        var name = model.Name;
        var resource = model.AssociatedResource;
        var parents = node.GetResourceParentChain();
        var resourceKey = ResourceKey.From(resource, name, parents);

        if (resource is not IResourceWithDto resourceWithDto)
        {
            return;
        }

        if (model is not IDtoTestModel dtoTestModel)
        {
            throw new InvalidOperationException($"Model for resource {resource} must be DTO test model.");
        }

        var apimContentsOption = await getDto(resourceWithDto, name, parents, cancellationToken);

        // Secret named values might not get published if they don't have a value
        if (resource is NamedValueResource
            && dtoTestModel is NamedValueModel namedValueModel
            && namedValueModel.IsSecret
            && apimContentsOption.IsNone)
        {
            return;
        }

        var apimContents = apimContentsOption.IfNone(() => throw new InvalidOperationException($"Could not find DTO for {resourceKey}."));
        var overrideJson = getOverride(resourceKey);
        var dtosMatch = dtoTestModel.MatchesDto(apimContents, overrideJson);
        if (dtosMatch is false)
        {
            throw new InvalidOperationException($"DTO for {ResourceKey.From(resource, name, parents)} does not match the expected DTO.");
        }

        Option<JsonObject> getOverride(ResourceKey resourceKey) =>
            parents.Aggregate(overrides,
                              (option, parent) => from section in option
                                                  from resourceJson in getResourceJson(parent.Resource, parent.Name, section)
                                                  select resourceJson)
                   .Bind(json => getResourceJson(resource, name, json));

        static Option<JsonObject> getResourceJson(IResource resource, ResourceName name, JsonObject json) =>
            from resources in json.GetJsonArrayProperty(resource.ConfigurationKey).ToOption()
            from resourceJson in resources.Pick(node => from jsonObject in node.AsJsonObject().ToOption()
                                                        from resourceName in jsonObject.GetStringProperty("name").ToOption()
                                                        where resourceName == name.ToString()
                                                        select jsonObject)
            select resourceJson;
    }

    public static void ConfigureValidatePublisherWithCommit(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);
        ResourceModule.ConfigureIsResourceSupportedInApim(builder);
        ResourceModule.ConfigureListResourceNamesFromApim(builder);
        ResourceModule.ConfigureGetOptionalResourceDtoFromApim(builder);
        ResourceModule.ConfigureDoesResourceExistInApim(builder);
        ServiceModule.ConfigureShouldSkipResource(builder);

        builder.TryAddSingleton(ResolveValidatePublisherWithCommit);
    }

    private static ValidatePublisherWithCommit ResolveValidatePublisherWithCommit(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var isSkuSupported = provider.GetRequiredService<IsResourceSupportedInApim>();
        var listNames = provider.GetRequiredService<ListResourceNamesFromApim>();
        var getDto = provider.GetRequiredService<GetOptionalResourceDtoFromApim>();
        var resourceExists = provider.GetRequiredService<DoesResourceExistInApim>();
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
                var resourceKey = new ResourceKey
                {
                    Parents = node.GetResourceParentChain(),
                    Name = node.Model.Name,
                    Resource = node.Model.AssociatedResource
                };

                if (await shouldSkipResource(resourceKey, cancellationToken))
                {
                    return;
                }

                if (await resourceExists(resourceKey, cancellationToken))
                {
                    throw new InvalidOperationException($"Expected {resourceKey} to have been deleted from APIM.");
                }
            }, maxDegreeOfParallelism: Option.None, cancellationToken);
        };

        async ValueTask validateApimMatchesSet(ModelNodeSet nodes, Option<JsonObject> overrides, CancellationToken cancellationToken) =>
            await nodes.IterTaskParallel(async node =>
            {
                var resourceKey = new ResourceKey
                {
                    Parents = node.GetResourceParentChain(),
                    Name = node.Model.Name,
                    Resource = node.Model.AssociatedResource
                };

                if (await shouldSkipResource(resourceKey, cancellationToken))
                {
                    return;
                }

                await ValidateApimMatchesNode(node, overrides, getDto, cancellationToken);
            }, maxDegreeOfParallelism: Option.None, cancellationToken);
    }
}