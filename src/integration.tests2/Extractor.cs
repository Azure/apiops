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

    public bool ShouldExtract(ResourceKey resourceKey)
    {
        var normalizedResources =
            Resources.GroupBy(kvp => normalizeParentChain(kvp.Key),
                              kvp => kvp.Value)
                     .ToImmutableDictionary(parentGroup => parentGroup.Key,
                                            parentGroup =>
                                                parentGroup
                                                    .SelectMany(resourceNames => resourceNames)
                                                    .GroupBy(kvp => kvp.Key, kvp => kvp.Value)
                                                    .ToImmutableDictionary(resourceGroup => resourceGroup.Key,
                                                                           resourceGroup => resourceGroup.SelectMany(names => names)
                                                                                                         .Select(name => normalizeName(resourceGroup.Key, name))
                                                                                                         .ToImmutableHashSet()));

        return shouldExtract(resourceKey);

        bool shouldExtract(ResourceKey resourceKey)
        {
            var normalizedKey = normalizeKey(resourceKey);

            return normalizedKey.Parents.All(parent =>
            {
                var parentParents = ParentChain.From(normalizedKey.Parents.TakeWhile(ancestor => ancestor != parent));
                var parentKey = ResourceKey.From(parent.Resource, parent.Name, parentParents);
                return shouldExtract(parentKey);
            })
            && (normalizedResources.TryGetValue(normalizedKey.Parents, out var resourceDictionary) is false
                || resourceDictionary.TryGetValue(normalizedKey.Resource, out var resourceNames) is false
                || resourceNames.Contains(normalizedKey.Name));
        }

        static ResourceKey normalizeKey(ResourceKey key) =>
            key with
            {
                Parents = normalizeParentChain(key.Parents),
                Name = normalizeName(key.Resource, key.Name)
            };

        static ParentChain normalizeParentChain(ParentChain parentChain) =>
            ParentChain.From(from tuple in parentChain
                             let resource = tuple.Resource
                             let normalizedName = normalizeName(resource, tuple.Name)
                             select (resource, normalizedName));

        static ResourceName normalizeName(IResource resource, ResourceName name) =>
            resource switch
            {
                ApiResource => ApiRevisionModule.GetRootName(name),
                _ => name
            };
    }

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
                                .Map(children => new JsonObject
                                {
                                    [name.ToString()] = new JsonObject(getResourceKvps(parentChainWithResource, children))
                                } as JsonNode)
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
            var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();
            var getApiSpecificationFromFile = provider.GetRequiredService<GetApiSpecificationFromFile>();
            var parseResourceFile = provider.GetRequiredService<ParseResourceFile>();
            var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();
            var fileOperations = getLocalFileOperations();

            var expectedModels = getExpectedModels(testState, filterOption);

            var result = await validateModelsWereExtracted(expectedModels, getInformationFileDto, getPolicyFileContents, getApiSpecificationFromFile, fileOperations, cancellationToken);
            result.IfErrorThrow();

            result = await validateOnlyModelsWereExtracted(expectedModels, parseResourceFile, fileOperations, cancellationToken);
            result.IfErrorThrow();
        };

        static ServiceProvider getLocalDependenciesProvider(ServiceDirectory serviceDirectory)
        {
            var builder = Host.CreateEmptyApplicationBuilder(new());

            builder.AddServiceDirectoryToConfiguration(serviceDirectory);
            ResourceModule.ConfigureGetInformationFileDto(builder);
            ResourceModule.ConfigureGetPolicyFileContents(builder);
            ResourceModule.ConfigureGetApiSpecificationFromFile(builder);
            ResourceModule.ConfigureParseResourceFile(builder);
            FileSystemModule.ConfigureGetLocalFileOperations(builder);

            return builder.Services.BuildServiceProvider();
        }

        static ImmutableArray<ITestModel> getExpectedModels(TestState testState, Option<ExtractorFilter> filterOption) =>
            filterOption.Map(filter => testState.Models
                                                .Where(model => filter.ShouldExtract(model.Key))
                                                .ToImmutableArray())
                         .IfNone(() => testState.Models);

        async ValueTask<Result<Unit>> validateModelsWereExtracted(ImmutableArray<ITestModel> expectedModels, GetInformationFileDto getInformationFileDto, GetPolicyFileContents getPolicyFileContents, GetApiSpecificationFromFile getApiSpecificationFromFile, FileOperations fileOperations, CancellationToken cancellationToken)
        {
            var result =
                await expectedModels
                        .ToAsyncEnumerable()
                        .Where(async (model, cancellationToken) => await isResourceKeySupported(model.Key, cancellationToken))
                        .Traverse(async model =>
                        {
                            var (resource, name, parents) = (model.Key.Resource, model.Key.Name, model.Key.Parents);

                            if (resource is ApiResource apiResource && model is ApiModel apiModel)
                            {
                                var dtoOption = await getInformationFileDto(apiResource, name, parents, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);
                                var dtoResult = dtoOption.ToResult(() => Error.From($"Could not find extracted file for {model.Key}."));

                                var specificationOption = await getApiSpecificationFromFile(model.Key, fileOperations.ReadFile, cancellationToken);
                                var specificationResult = (specificationOption.IfNoneNullable(), apiModel.Specification.IfNoneNull(), apiModel.OperationNames.IfNoneNull()) switch
                                {
                                    // No extracted specification file found
                                    (null, null, _) => Result.Success(Unit.Instance),
                                    (null, not null, _) when apiModel.Type is ApiType.Wsdl => Result.Success(Unit.Instance), // SOAP APIs should never have specifications extracted.
                                    (null, not null, _) => Error.From($"Could not find extracted specification file for {model.Key}."),
                                    // Extracted specification file found
                                    (not null, null, _) => Error.From($"Found specification file for '{model.Key}', but model has no specification."),
                                    (not null, _, _) when apiModel.Type is ApiType.Wsdl => Error.From($"Found specification file for SOAP API '{model.Key}'."), // SOAP APIs should never have specifications extracted.
                                    (not null, not null, null) => Result.Success(Unit.Instance),
                                    (not null, not null, not null) when apiModel.Type is ApiType.Wsdl or ApiType.Wadl => Result.Success(Unit.Instance), // APIM randomly generates operation names for SOAP and WADL APIs, so we cannot reliably validate them.
                                    (var (_, contents), _, var operationNames) => operationNames.Traverse(name => contents.ToString().Contains($"{name}", StringComparison.OrdinalIgnoreCase)
                                                                                                                    ? Result.Success(Unit.Instance)
                                                                                                                    : Error.From($"Specification file for {model.Key} does not contain operation {name}."),
                                                                                                          cancellationToken)
                                                                                                .Map(_ => Unit.Instance)
                                };

                                var validationResult = from _ in dtoResult
                                                        from __ in specificationResult
                                                        select Unit.Instance;

                                return validationResult.MapError(error => Error.From($"Validation failed for {model.Key}. {error}"));
                            }

                            if (resource is PolicyFragmentResource policyFragmentResource)
                            {
                                var informationFileDtoOption = await getInformationFileDto(policyFragmentResource, name, parents, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);
                                var informationFileResult = informationFileDtoOption.ToResult(() => Error.From($"Could not find extracted information file for {model.Key}."));

                                var policyContentsOption = await getPolicyFileContents(policyFragmentResource, name, parents, fileOperations.ReadFile, cancellationToken);
                                var policyContentsResult = policyContentsOption.Map(PolicyContentsToDto)
                                                                               .ToResult(() => Error.From($"Could not find extracted policy file for {model.Key}."));

                                return from informationFileDto in informationFileResult
                                       from policyContentsDto in policyContentsResult
                                       let mergedDto = informationFileDto.MergeWith(policyContentsDto)
                                       from validationResult in model.ValidateDto(mergedDto)
                                                                     .MapError(error => Error.From($"Validation failed for {model.Key}. {error}"))
                                       select validationResult;
                            }

                            if (resource is IResourceWithInformationFile resourceWithInformationFile)
                            {
                                var dtoOption = await getInformationFileDto(resourceWithInformationFile, name, parents, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);
                                var dtoResult = dtoOption.ToResult(() => Error.From($"Could not find extracted file for {model.Key}."));

                                return dtoResult.Bind(model.ValidateDto);
                            }

                            if (resource is IPolicyResource policyResource)
                            {
                                var contentsOption = await getPolicyFileContents(policyResource, name, parents, fileOperations.ReadFile, cancellationToken);
                                var contentsResult = contentsOption.Map(PolicyContentsToDto)
                                                                   .ToResult(() => Error.From($"Could not find extracted policy file for {model.Key}."));

                                return contentsResult.Bind(dto => model.ValidateDto(dto)
                                                                       .MapError(error => Error.From($"Validation failed for {model.Key}. {error}")));
                            }

                            return Unit.Instance;
                        }, cancellationToken);

            return result.Map(_ => Unit.Instance);
        }

        static JsonObject PolicyContentsToDto(BinaryData contents) =>
            new()
            {
                ["properties"] = new JsonObject
                {
                    ["value"] = contents.ToString()
                }
            };

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