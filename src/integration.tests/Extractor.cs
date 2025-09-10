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
        ResourceGraphModule.ConfigureBuilder(builder);
        builder.TryAddSingleton(GetRunExtractor);
    }

    private static RunExtractor GetRunExtractor(IServiceProvider provider)
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
            graph.GetTraversalRootResources()
                 .Aggregate(new JsonObject(),
                            (json, resource) =>
                            {
                                var nodes = getModelNodeSet(resource, models);

                                return json.SetProperty(resource.PluralName,
                                                        modelNodeSetToJson(nodes, models),
                                                        mutateOriginal: true);
                            });

        ModelNodeSet getModelNodeSet(IResource resource, ResourceModels models) =>
            models.Find(resource)
                  .IfNone(() => ModelNodeSet.Empty);

        JsonArray modelNodeSetToJson(ModelNodeSet hierarchy, ResourceModels models) =>
            hierarchy.Select(node => modelNodeToJson(node, models))
                     .ToJsonArray();

        JsonNode modelNodeToJson(ModelNode node, ResourceModels models)
        {
            var name = node.Model.Name.ToString();

            var successors = from resource in graph.GetTraversalSuccessors(node.Model.AssociatedResource)
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
                                                      [successor.Resource.PluralName] = successor.Hierarchy
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
        ResourceGraphModule.ConfigureBuilder(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ServiceModule.ConfigureShouldSkipResource(builder);

        builder.TryAddSingleton(GetValidateExtractor);
    }

    private static ValidateExtractor GetValidateExtractor(IServiceProvider provider)
    {
        var graph = provider.GetRequiredService<ResourceGraph>();
        var shouldSkipResource = provider.GetRequiredService<ShouldSkipResource>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (models, extractorSubset, serviceDirectory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("validate.extractor");

            var modelsToValidate = extractorSubset.IfNone(() => models);
            await validateResourceModelsWereExtracted(modelsToValidate, serviceDirectory, cancellationToken);
            await validateOnlyResourceModelsWereExtracted(modelsToValidate, serviceDirectory, cancellationToken);
        };

        async ValueTask validateResourceModelsWereExtracted(ResourceModels models, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await models.SelectMany(kvp => from node in kvp.Value
                                           select (Resource: kvp.Key, node.Model, Ancestors: node.GetResourceAncestors()))
                        .IterTaskParallel(async x =>
                        {
                            var (resource, model, ancestors) = x;
                            var name = model.Name;

                            if (await shouldSkipResource(resource, name, ancestors, cancellationToken))
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
                                    var informationFileOption = await policyFragmentResource.GetInformationFileDto(name, ancestors, serviceDirectory, FileInfoModule.ReadAsBinaryData, cancellationToken);
                                    var policyFileOption = await policyFragmentResource.GetPolicyFileDto(name, ancestors, serviceDirectory, FileInfoModule.ReadAsBinaryData, cancellationToken);

                                    contentsOption = (informationFileOption.IfNoneNull(), policyFileOption.IfNoneNull()) switch
                                    {
                                        (null, null) => contentsOption,
                                        (JsonObject informationFile, null) => informationFile,
                                        (null, JsonObject policyFile) => policyFile,
                                        (JsonObject informationFile, JsonObject policyFile) => informationFile.MergeWith(policyFile)
                                    };

                                    break;
                                case ILinkResource linkResource:
                                    contentsOption = await linkResource.GetInformationFileDto(name, ancestors, serviceDirectory, directory => Option.Some(directory.GetChildDirectories()), FileInfoModule.ReadAsBinaryData, cancellationToken);
                                    break;
                                case IResourceWithInformationFile resourceWithInformationFile:
                                    contentsOption = await resourceWithInformationFile.GetInformationFileDto(name, ancestors, serviceDirectory, FileInfoModule.ReadAsBinaryData, cancellationToken);
                                    break;
                                case IPolicyResource policyResource:
                                    contentsOption = await policyResource.GetPolicyFileDto(name, ancestors, serviceDirectory, FileInfoModule.ReadAsBinaryData, cancellationToken);
                                    break;
                                default:
                                    break;
                            }

                            var contents = contentsOption.IfNoneThrow(() => new InvalidOperationException($"Could not find DTO for {resource.SingularName} '{name}'{ancestors.ToLogString()}."));

                            if (dtoTestModel.MatchesDto(contents, overrideJson: Option.None) is false)
                            {
                                throw new InvalidOperationException($"DTO for {resource.SingularName} '{name}'{ancestors.ToLogString()} does not match the expected DTO.");
                            }
                        }, maxDegreeOfParallelism: Option.None, cancellationToken);

        async ValueTask validateOnlyResourceModelsWereExtracted(ResourceModels models, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await serviceDirectory.ToDirectoryInfo()
                                  // Get all files in the service directory
                                  .EnumerateFiles("*", SearchOption.AllDirectories)
                                  // Get the resource associated with the file
                                  .Choose(async file => await parseFile(file, serviceDirectory, cancellationToken))
                                  .IterTaskParallel(async x =>
                                  {
                                      var (resource, name, ancestors) = x;

                                      var exception = new InvalidOperationException($"Resource {resource.SingularName} {name}{ancestors.ToLogString()} was extracted but not found in the models.");

                                      switch (resource)
                                      {
                                          case ApiResource:
                                              // For APIs, we validate that the root name is in the models, regardless of the revisions
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

                                      await ValueTask.CompletedTask;
                                  }, maxDegreeOfParallelism: Option.None, cancellationToken);

        async ValueTask<Option<(IResource resource, ResourceName Name, ResourceAncestors Ancestors)>> parseFile(FileInfo file, ServiceDirectory serviceDirectory, CancellationToken cancellationToken) =>
            await graph.TopologicallySortedResources
                       .Choose(async resource => from x in resource switch
                       {
                           ILinkResource linkResource =>
                              await linkResource.ParseInformationFile(file, serviceDirectory, FileInfoModule.ReadAsBinaryData, cancellationToken),
                           IResourceWithInformationFile resourceWithInformationFile =>
                              resourceWithInformationFile.ParseInformationFile(file, serviceDirectory),
                           IPolicyResource policyResource =>
                              policyResource.ParsePolicyFile(file, serviceDirectory),
                           _ => Option.None
                       }
                                                 select (resource, x.Name, x.Ancestors))
                       .Head(cancellationToken);
    }
}
