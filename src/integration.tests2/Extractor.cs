using common;
using common.tests;
using CsCheck;
using Microsoft.Extensions.Configuration;
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

namespace integration.tests;

internal delegate ValueTask RunExtractor(ServiceDirectory serviceDirectory, Option<ExtractorFilter> filterOption, CancellationToken cancellationToken);
internal delegate ValueTask ValidateExtractor(TestState testState, ServiceDirectory serviceDirectory, Option<ExtractorFilter> filterOption, CancellationToken cancellationToken);

internal sealed record ExtractorFilter
{
    public required ImmutableDictionary<ParentChain, ImmutableDictionary<IResource, ImmutableHashSet<ResourceName>>> Resources { get; init; }

    public bool ShouldExtract(ResourceKey resourceKey) =>
        Resources.TryGetValue(resourceKey.Parents, out var resourceDictionary) is false
        || resourceDictionary.TryGetValue(resourceKey.Resource, out var resourceNames) is false
        || resourceNames.Contains(resourceKey.Name);

    public JsonObject Serialize()
    {
        var rootResources = Resources.Find(ParentChain.Empty)
                                     .IfNone(() => []);

        return [.. getResourceKvps(ParentChain.Empty, rootResources)];

        IEnumerable<KeyValuePair<string, JsonNode?>> getResourceKvps(ParentChain parentChain, ImmutableDictionary<IResource, ImmutableHashSet<ResourceName>> resources) =>
            from kvp in resources
            let jsonKey = kvp.Key.ConfigurationKey
            let jsonValue = kvp.Value.Select(name =>
            {
                var parentChainWithResource = parentChain.Append(kvp.Key, name);

                return Resources.Find(parentChainWithResource)
                                .Map(children => new JsonObject(getResourceKvps(parentChainWithResource, children)) as JsonNode)
                                .IfNone(() => JsonValue.Create(name.ToString()));
            }).ToJsonArray()
            select KeyValuePair.Create(jsonKey, jsonValue as JsonNode);
    }

    public static Gen<ExtractorFilter> Generate(IEnumerable<ITestModel> models)
    {
        var resources = models.GroupBy(model => model.Key.Parents,
                                       model => model.Key)
                              .ToImmutableDictionary(group => group.Key,
                                                     group => group.GroupBy(key => key.Resource, key => key.Name)
                                                                   .ToImmutableDictionary(group => group.Key,
                                                                                          group => group.ToImmutableHashSet()));

        return from generatedResources in generateResources(ParentChain.Empty)
               select new ExtractorFilter
               {
                   Resources = [.. generatedResources]
               };

        Gen<ImmutableArray<KeyValuePair<ParentChain, ImmutableDictionary<IResource, ImmutableHashSet<ResourceName>>>>> generateResources(ParentChain parentChain) =>
            from children in generateParentChainImmediateChildren(parentChain)
            from descendentsArray in Generator.Traverse(children,
                                                        kvp => from descendent in Generator.Traverse(kvp.Value.Select(name => parentChain.Append(kvp.Key, name))
                                                                                                              .Where(resources.ContainsKey),
                                                                                                     generateResources)
                                                               select descendent.SelectMany(array => array))
            select ImmutableArray.CreateRange([KeyValuePair.Create(parentChain, children.ToImmutableDictionary()),
                                               .. descendentsArray.SelectMany(array => array)]);

        Gen<ImmutableArray<KeyValuePair<IResource, ImmutableHashSet<ResourceName>>>> generateParentChainImmediateChildren(ParentChain parentChain) =>
            from children in Generator.SubSetOf(resources.Find(parentChain)
                                                         .IfNone(() => []))
            from resourceNames in Generator.Traverse(children,
                                                     kvp => from names in Generator.SubSetOf(kvp.Value)
                                                            select KeyValuePair.Create(kvp.Key, names))
            select resourceNames;
    }

    public override string ToString() =>
        Serialize().ToJsonString();
}

internal static class ExtractorModule
{
    public static void ConfigureRunExtractor(IHostApplicationBuilder builder)
    {
        builder.TryAddSingleton(ResolveRunExtractor);
    }

    private static RunExtractor ResolveRunExtractor(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (serviceDirectory, filterOption, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("extractor.run")
                                              ?.SetTag("service.directory", serviceDirectory)
                                              ?.SetTag("filter.option", filterOption);

            await filterOption.IterTask(async filter => await writeConfigurationFile(serviceDirectory, filter, cancellationToken));

            var arguments = getArguments(serviceDirectory, filterOption);
            await extractor.Program.Main([.. arguments]);
        };

        static async ValueTask writeConfigurationFile(ServiceDirectory serviceDirectory, ExtractorFilter filter, CancellationToken cancellationToken)
        {
            var file = getConfigurationFile(serviceDirectory);

            var json = filter.Serialize();
            var yaml = YamlDotNet.System.Text.Json.YamlConverter.Serialize(json);
            var binaryData = BinaryData.FromString(yaml);

            await file.OverwriteWithBinaryData(binaryData, cancellationToken);
        }

        IEnumerable<string> getArguments(ServiceDirectory serviceDirectory, Option<ExtractorFilter> filterOption) =>
            [
                $"--API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH={serviceDirectory.ToDirectoryInfo().FullName}",
                $"--API_MANAGEMENT_SERVICE_NAME={configuration.GetValueOrThrow("API_MANAGEMENT_SERVICE_NAME")}",
                $"--AZURE_RESOURCE_GROUP_NAME={configuration.GetValueOrThrow("AZURE_RESOURCE_GROUP_NAME")}",
                $"--AZURE_SUBSCRIPTION_ID={configuration.GetValueOrThrow("AZURE_SUBSCRIPTION_ID")}",
                .. configuration.GetValue("AZURE_BEARER_TOKEN")
                                .Map(token => ImmutableArray.Create($"--AZURE_BEARER_TOKEN={token}"))
                                .IfNone(() => []),
                .. configuration.GetValue("AZURE_CLOUD_ENVIRONMENT")
                                .Map(environment => ImmutableArray.Create($"--AZURE_CLOUD_ENVIRONMENT={environment}"))
                                .IfNone(() => []),
                .. filterOption.Map(_ => getConfigurationFile(serviceDirectory))
                               .Map(file => ImmutableArray.Create($"--{ConfigurationModule.YamlPath}={file.FullName}"))
                               .IfNone(() => [])
            ];

        static FileInfo getConfigurationFile(ServiceDirectory serviceDirectory) =>
            new(Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.extractor.yaml"));
    }

    public static void ConfigureValidateExtractor(IHostApplicationBuilder builder)
    {
        ApimModule.ConfigureIsResourceKeySupported(builder);

        builder.TryAddSingleton(ResolveValidateExtractor);
    }

    private static ValidateExtractor ResolveValidateExtractor(IServiceProvider provider)
    {
        var isResourceKeySupported = provider.GetRequiredService<IsResourceKeySupported>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (testState, serviceDirectory, filterOption, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("extractor.validate")
                                              ?.SetTag("test.state", testState)
                                              ?.SetTag("service.directory", serviceDirectory)
                                              ?.SetTag("filter.option", filterOption);

            using var provider = getLocalDependenciesProvider(serviceDirectory);
            var getInformationFileDto = provider.GetRequiredService<GetInformationFileDto>();
            var parseResourceFile = provider.GetRequiredService<ParseResourceFile>();
            var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();
            var fileOperations = getLocalFileOperations();

            var expectedModels = getExpectedModels(testState, filterOption);

            var result = await validateModelsWereExtracted(expectedModels, getInformationFileDto, fileOperations, cancellationToken);
            result.IfErrorThrow();

            result = await validateOnlyModelsWereExtracted(expectedModels, parseResourceFile, fileOperations, cancellationToken);
            result.IfErrorThrow();
        };

        static ServiceProvider getLocalDependenciesProvider(ServiceDirectory serviceDirectory)
        {
            var builder = Host.CreateEmptyApplicationBuilder(new());

            builder.AddServiceDirectoryToConfiguration(serviceDirectory);
            ResourceModule.ConfigureGetInformationFileDto(builder);
            ResourceModule.ConfigureParseResourceFile(builder);
            FileSystemModule.ConfigureGetLocalFileOperations(builder);

            return builder.Services.BuildServiceProvider();
        }

        static ImmutableArray<ITestModel> getExpectedModels(TestState testState, Option<ExtractorFilter> filterOption) =>
            filterOption.Map(filter => testState.Models
                                                .Where(model => filter.ShouldExtract(model.Key))
                                                .ToImmutableArray())
                         .IfNone(() => testState.Models);

        async ValueTask<Result<Unit>> validateModelsWereExtracted(ImmutableArray<ITestModel> expectedModels, GetInformationFileDto getInformationFileDto, FileOperations fileOperations, CancellationToken cancellationToken)
        {
            var result =
                await expectedModels
                        .ToAsyncEnumerable()
                        .Where(async (model, cancellationToken) => await isResourceKeySupported(model.Key, cancellationToken))
                        .Traverse(async model =>
                        {
                            switch (model.Key.Resource)
                            {
                                case IResourceWithInformationFile resourceWithInformationFile:
                                    var (resource, name, parents) = (model.Key.Resource, model.Key.Name, model.Key.Parents);

                                    var dtoOption = await getInformationFileDto(resourceWithInformationFile, name, parents, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);

                                    return dtoOption.Map(model.ValidateDto)
                                                    .IfNone(() => Error.From($"Could not find extracted file for {model.Key}."));
                                default:
                                    return Unit.Instance;
                            }
                        }, cancellationToken);

            return result.Map(_ => Unit.Instance);
        }

        async ValueTask<Result<Unit>> validateOnlyModelsWereExtracted(ImmutableArray<ITestModel> expectedModels, ParseResourceFile parseResourceFile, FileOperations fileOperations, CancellationToken cancellationToken)
        {
            var modelKeys = expectedModels
                                .Select(model => model.Key)
                                .ToImmutableHashSet();

            var result =
                await fileOperations.EnumerateServiceDirectoryFiles()
                                    .ToAsyncEnumerable()
                                    .Choose(async file => await parseResourceFile(file, fileOperations.ReadFile, cancellationToken))
                                    .Where(async (key, cancellationToken) => await isResourceKeySupported(key, cancellationToken))
                                    .Traverse(async key => modelKeys.Contains(key)
                                                            ? Result.Success(Unit.Instance)
                                                            : Error.From($"Resource '{key}' was extracted but has no corresponding model."), cancellationToken);

            return result.Map(_ => Unit.Instance);
        }
    }
}