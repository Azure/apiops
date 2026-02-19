using common;
using DotNext.Collections.Generic;
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

internal delegate ValueTask RunExtractor(ExtractorOptions options, CancellationToken cancellationToken);
internal delegate ValueTask ValidateExtractor(ResourceModels models, Option<ResourceModels> extractorSubset, ServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal sealed record ExtractorOptions
{
    public required ServiceDirectory ServiceDirectory { get; init; }
    public Option<ServiceName> ServiceName { get; init; } = Option.None;
    public Option<ResourceModels> Models { get; init; } = Option.None;
}

internal static class ExtractorModule
{
    public static void ConfigureRunExtractor(IHostApplicationBuilder builder)
    {
        ResourceGraphModule.ConfigureResourceGraph(builder);

        builder.TryAddSingleton(ResolveRunExtractor);
    }

    private static RunExtractor ResolveRunExtractor(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (options, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("run.extractor");

            await writeConfigurationYaml(options, cancellationToken);

            var arguments = getProgramArguments(options);
            await extractor.Program.Main([.. arguments]);
        };

        async ValueTask writeConfigurationYaml(ExtractorOptions options, CancellationToken cancellationToken) =>
            await options.Models
                         .IterTask(async models =>
                         {
                             var file = getConfigurationFile(options);
                             var json = resourceModelsToJson(models);
                             var yaml = YamlConverter.Serialize(json);
                             var content = BinaryData.FromString(yaml);
                             await file.OverwriteWithBinaryData(content, cancellationToken);
                         });

        JsonObject resourceModelsToJson(ResourceModels models) =>
            graph.ListTraversalRootResources()
                 .Aggregate(new JsonObject(),
                            (json, resource) =>
                            {
                                var nodes = getModelNodeSet(resource, models);

                                return json.SetProperty(resource.ConfigurationKey,
                                                        modelNodeSetToJson(nodes, models),
                                                        mutateOriginal: true);
                            });

        ModelNodeSet getModelNodeSet(IResource resource, ResourceModels models) =>
            models.Find(resource)
                  .IfNone(() => ModelNodeSet.Empty);

        JsonArray modelNodeSetToJson(ModelNodeSet hierarchy, ResourceModels models) =>
            hierarchy
                     // Do not write revisioned APIs. Only the root API name is supported.
                     .Where(node => node.Model.AssociatedResource is not ApiResource || ApiRevisionModule.IsRootName(node.Model.Name))
                     .Select(node => modelNodeToJson(node, models))
                     .ToJsonArray();

        JsonNode modelNodeToJson(ModelNode node, ResourceModels models)
        {
            var name = node.Model.Name.ToString();

            var successors = from resource in graph.ListTraversalSuccessors(node.Model.AssociatedResource)
                             let successorHierarchy = from successor in getModelNodeSet(resource, models)
                                                      where successor.Predecessors.Contains(node)
                                                      select successor
                             select (Resource: resource, Hierarchy: successorHierarchy);

            // If there are no successors, we return the name as a simple JSON value (e.g. "api1");
            // Otherwise, we return a JSON object with the name as a key and an array of successors grouped by their resource.
            // (e.g. { "api1": { "operations": [...] , "policies": [...] } }).
            return successors.ToArray() switch
            {
                [] => JsonValue.Create(name),
                _ => new JsonObject
                {
                    [name] = successors.Aggregate(new JsonObject(),
                                                  (current, successor) => current.MergeWith(new JsonObject
                                                  {
                                                      [successor.Resource.ConfigurationKey] = successor.Hierarchy
                                                                                                 .Select(successor => modelNodeToJson(successor, models))
                                                                                                 .ToJsonArray()
                                                  }, mutateOriginal: true))
                }
            };
        }

        FileInfo getConfigurationFile(ExtractorOptions options) =>
            options.ServiceDirectory
                   .ToDirectoryInfo()
                   .GetChildFile("configuration.extractor.yaml");

        ImmutableArray<string> getProgramArguments(ExtractorOptions options)
        {
            var arguments = new List<string>();

            arguments.AddRange(["--API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH", options.ServiceDirectory.ToDirectoryInfo().FullName]);
            options.Models.Iter(_ => arguments.AddRange([$"--{ConfigurationModule.YamlPath}", getConfigurationFile(options).FullName]));
            options.ServiceName.Iter(name => arguments.AddRange(["--API_MANAGEMENT_SERVICE_NAME", name.ToString()]));

            return [.. arguments];
        }
    }

    public static void ConfigureValidateExtractor(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureGetInformationFileDto(builder);
        ResourceModule.ConfigureGetPolicyFileContents(builder);
        ResourceModule.ConfigureParseResourceFile(builder);
        ServiceModule.ConfigureShouldSkipResource(builder);
        common.FileSystemModule.ConfigureGetLocalFileOperations(builder);

        builder.TryAddSingleton(ResolveValidateExtractor);
    }

    private static ValidateExtractor ResolveValidateExtractor(IServiceProvider provider)
    {
        var getInformationFileDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();
        var parseFile = provider.GetRequiredService<ParseResourceFile>();
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var fileOperations = getLocalFileOperations();

        return async (models, extractorSubset, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("validate.extractor");

            var modelsToValidate = extractorSubset.IfNone(() => models);
            await validateResourceModelsWereExtracted(modelsToValidate, serviceDirectory, cancellationToken);
            await validateOnlyResourceModelsWereExtracted(modelsToValidate, serviceDirectory, cancellationToken);
        };

        async ValueTask validateResourceModelsWereExtracted(ResourceModels models, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await models.SelectMany(kvp => from node in kvp.Value
                                           select (Resource: kvp.Key, node.Model, Ancestors: node.GetResourceParentChain()))
                        .IterTaskParallel(async x =>
                        {
                            var (resource, model, ancestors) = x;
                            var name = model.Name;
                            var resourceKey = ResourceKey.From(resource, name, ancestors);

                            if (await shouldSkipResource(resourceKey, cancellationToken))
                            {
                                return;
                            }

                            if (model is not IDtoTestModel dtoTestModel)
                            {
                                return;
                            }

                            var contentsOption = Option<JsonObject>.None();
                            switch (resource)
                            {
                                case PolicyFragmentResource policyFragmentResource:
                                    var informationFileOption = await getInformationFileDto(policyFragmentResource, name, ancestors, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);
                                    var policyFileOption = from policyContents in await getPolicyFileContents(policyFragmentResource, name, ancestors, fileOperations.ReadFile, cancellationToken)
                                                           select new JsonObject
                                                           {
                                                               ["properties"] = new JsonObject
                                                               {
                                                                   ["format"] = "rawxml",
                                                                   ["value"] = policyContents.ToString()
                                                               }
                                                           };

                                    contentsOption = (informationFileOption.IfNoneNull(), policyFileOption.IfNoneNull()) switch
                                    {
                                        (null, null) => contentsOption,
                                        (JsonObject informationFile, null) => informationFile,
                                        (null, JsonObject policyFile) => policyFile,
                                        (JsonObject informationFile, JsonObject policyFile) => informationFile.MergeWith(policyFile)
                                    };

                                    break;
                                case IResourceWithInformationFile resourceWithInformationFile:
                                    contentsOption = await getInformationFileDto(resourceWithInformationFile, name, ancestors, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);
                                    break;
                                case IPolicyResource policyResource:
                                    contentsOption = from policyContents in await getPolicyFileContents(policyResource, name, ancestors, fileOperations.ReadFile, cancellationToken)
                                                     select new JsonObject
                                                     {
                                                         ["properties"] = new JsonObject
                                                         {
                                                             ["format"] = "rawxml",
                                                             ["value"] = policyContents.ToString()
                                                         }
                                                     };
                                    break;
                                default:
                                    break;
                            }

                            var contents = contentsOption.IfNoneThrow(() => new InvalidOperationException($"Could not find DTO for {resourceKey}."));

                            bool matchesDto = dtoTestModel.MatchesDto(contents, overrideJson: Option.None);
                            if (matchesDto is false)
                            {
                                throw new InvalidOperationException($"DTO for {resourceKey} does not match the expected DTO.");
                            }
                        }, maxDegreeOfParallelism: Option.None, cancellationToken);

        async ValueTask validateOnlyResourceModelsWereExtracted(ResourceModels models, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await serviceDirectory.ToDirectoryInfo()
                                  // Get all files in the service directory
                                  .EnumerateFiles("*", SearchOption.AllDirectories)
                                  // Get the resource associated with the file
                                  .Choose(async file => await parseFile(file, fileOperations.ReadFile, cancellationToken))
                                  .IterTaskParallel(async resourceKey =>
                                  {
                                      var exception = new InvalidOperationException($"Resource {resourceKey} was extracted but not found in the models.");

                                      var (resource, name, ancestors) = (resourceKey.Resource, resourceKey.Name, resourceKey.Parents);

                                      switch (resource)
                                      {
                                          case ApiResource:
                                              // For APIs, we validate that the root name is in the models, regardless of the revisions
                                              var rootName = ApiRevisionModule.GetRootName(name);

                                              models.Find(resource)
                                                    .Where(apis => apis.Any(node => ApiRevisionModule.GetRootName(node.Model.Name) == rootName && ancestors == node.GetResourceParentChain()))
                                                    .IfNone(() => throw exception);

                                              break;
                                          case IChildResource childResource when childResource.Parent is ApiResource:
                                              // For API child resources, we validate that the parent API (or its root) is in the models
                                              models.Find(resource, name, ancestors)
                                                    .IfNone(() =>
                                                    {
                                                        var api = ancestors.Last();
                                                        var rootName = ApiRevisionModule.GetRootName(api.Name);
                                                        var ancestorsWithRootApi = ParentChain.From([.. ancestors.SkipLast(1)])
                                                                                                    .Append(api.Resource, rootName);

                                                        return models.Find(resource, name, ancestorsWithRootApi);
                                                    })
                                                    .IfNone(() => throw exception);

                                              break;
                                          default:
                                              models.Find(resource, name, ancestors)
                                                    .IfNone(() => throw exception);

                                              break;
                                      }

                                      await ValueTask.CompletedTask;
                                  }, maxDegreeOfParallelism: Option.None, cancellationToken);
    }
}
