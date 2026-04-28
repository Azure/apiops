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

internal delegate ValueTask RunPublisher(ServiceDirectory serviceDirectory, Option<PublisherOverride> overrideOption, Option<CommitId> commitIdOption, CancellationToken cancellationToken);
internal delegate ValueTask ValidatePublisher(TestState testState, ServiceDirectory serviceDirectory, Option<PublisherOverride> overrideOption, CancellationToken cancellationToken);
internal delegate ValueTask<CommitId> WriteGitCommit(ServiceDirectory serviceDirectory, TestState state, CancellationToken cancellationToken);
internal delegate ValueTask ValidatePublisherStateTransition(TestState previousState, TestState currentState, CancellationToken cancellationToken);

internal sealed record PublisherOverride
{
    public required ImmutableDictionary<ResourceKey, ITestModel> Updates { get; init; }

    public JsonObject Serialize()
    {
        var keyJsons = new Dictionary<ResourceKey, JsonObject>();
        Updates.Iter(kvp =>
        {
            var (key, value) = kvp;
            keyJsons[key] = value.ToDto();
            key.Parents.Iter(parent =>
            {
                var parentParents = ParentChain.From(key.Parents.TakeWhile(ancestor => ancestor != parent));
                var parentKey = ResourceKey.From(parent.Resource, parent.Name, parentParents);
                if (keyJsons.ContainsKey(parentKey) is false)
                {
                    keyJsons[parentKey] = [];
                }
            });
        });

        var parentDictionary =
            keyJsons.GroupBy(kvp => kvp.Key.Parents,
                             kvp => (kvp.Key.Resource, kvp.Key.Name, Dto: kvp.Value))
                    .ToImmutableDictionary(group => group.Key,
                                           group => group.GroupBy(tuple => tuple.Resource,
                                                                  tuple => (tuple.Name, tuple.Dto))
                                                         .ToImmutableDictionary(group => group.Key,
                                                                                group => group.ToImmutableHashSet()));

        var rootResources = parentDictionary.Find(ParentChain.Empty)
                                            .IfNone(() => []);

        return [.. getResourceKvps(ParentChain.Empty, rootResources)];

        IEnumerable<KeyValuePair<string, JsonNode?>> getResourceKvps(ParentChain parentChain, ImmutableDictionary<IResource, ImmutableHashSet<(ResourceName Name, JsonObject Dto)>> resources) =>
            from kvp in resources
            let jsonKey = kvp.Key.ConfigurationKey
            let jsonValue = kvp.Value.Select(tuple =>
            {
                var (resource, (name, dto)) = (kvp.Key, tuple);
                var parentChainWithResource = parentChain.Append(resource, name);

                // Add name property
                dto["name"] = name.ToString();

                // Add children
                parentDictionary.Find(parentChainWithResource)
                                .Iter(children => getResourceKvps(parentChainWithResource, children)
                                                    .Iter(kvp => dto[kvp.Key] = kvp.Value));

                return dto;
            }).ToJsonArray()
            select KeyValuePair.Create(jsonKey, jsonValue as JsonNode);
    }

    public override string ToString() =>
        Serialize().ToJsonString();
}

internal static class PublisherModule
{
    public static void ConfigureRunPublisher(IHostApplicationBuilder builder)
    {
        builder.TryAddSingleton(ResolveRunPublisher);
    }

    private static RunPublisher ResolveRunPublisher(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (serviceDirectory, overrideOption, commitIdOption, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("publisher.run")
                                              ?.SetTag("service.directory", serviceDirectory)
                                              ?.SetTag("override.option", overrideOption)
                                              ?.SetTag("commit.id.option", commitIdOption);

            await overrideOption.IterTask(async publisherOverride => await writeConfigurationFile(serviceDirectory, publisherOverride, cancellationToken));

            var arguments = getArguments(serviceDirectory, overrideOption, commitIdOption);
            await publisher.Program.Main([.. arguments]);
        };

        static async ValueTask writeConfigurationFile(ServiceDirectory serviceDirectory, PublisherOverride publisherOverride, CancellationToken cancellationToken)
        {
            var file = getConfigurationFile(serviceDirectory);

            var json = publisherOverride.Serialize();
            var yaml = YamlDotNet.System.Text.Json.YamlConverter.Serialize(json);
            var binaryData = BinaryData.FromString(yaml);

            await file.OverwriteWithBinaryData(binaryData, cancellationToken);
        }

        IEnumerable<string> getArguments(ServiceDirectory serviceDirectory, Option<PublisherOverride> overrideOption, Option<CommitId> commitIdOption) =>
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
                .. overrideOption.Map(_ => getConfigurationFile(serviceDirectory))
                                .Map(file => ImmutableArray.Create($"--{ConfigurationModule.YamlPath}={file.FullName}"))
                                .IfNone(() => []),
                .. commitIdOption.Map(commitId => ImmutableArray.Create($"--COMMIT_ID={commitId}"))
                                .IfNone(() => [])
            ];

        static FileInfo getConfigurationFile(ServiceDirectory serviceDirectory) =>
            new(Path.Combine(serviceDirectory.ToDirectoryInfo().FullName, "configuration.publisher.yaml"));
    }

    public static void ConfigureValidatePublisher(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureGetOptionalResourceDtoFromApim(builder);
        ResourceModule.ConfigureListResourceNamesFromApim(builder);
        ApimModule.ConfigureIsResourceKeySupported(builder);

        builder.TryAddSingleton(ResolveValidatePublisher);
    }

    private static ValidatePublisher ResolveValidatePublisher(IServiceProvider provider)
    {
        var getDtoFromApim = provider.GetRequiredService<GetOptionalResourceDtoFromApim>();
        var listNamesFromApim = provider.GetRequiredService<ListResourceNamesFromApim>();
        var isResourceKeySupported = provider.GetRequiredService<IsResourceKeySupported>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (testState, serviceDirectory, overrideOption, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("publisher.validate")
                                              ?.SetTag("test.state", testState)
                                              ?.SetTag("service.directory", serviceDirectory)
                                              ?.SetTag("override.option", overrideOption);

            var expectedModels = getExpectedModels(testState, overrideOption);

            var result = await validateModelsWerePublished(expectedModels, cancellationToken);
            result.IfErrorThrow();

            result = await validateOnlyExpectedResourcesExist(expectedModels, cancellationToken);
            result.IfErrorThrow();
        };

        static ImmutableArray<ITestModel> getExpectedModels(TestState testState, Option<PublisherOverride> overrideOption)
        {
            var overrides = overrideOption.Map(publisherOverride => publisherOverride.Updates)
                                          .IfNone(() => []);

            return [.. from model in testState.Models
                       let overriddenModel  = overrides.Find(model.Key)
                                                       .IfNone(() => model)
                       // Only include secret named values if there is an override
                       where overriddenModel is not NamedValueModel { Secret: true }
                             || overrides.ContainsKey(overriddenModel .Key)
                       select overriddenModel ];
        }

        async ValueTask<Result<Unit>> validateModelsWerePublished(ImmutableArray<ITestModel> models, CancellationToken cancellationToken)
        {
            var result =
                await models.ToAsyncEnumerable()
                            .Where(async (model, cancellationToken) => await isResourceKeySupported(model.Key, cancellationToken))
                            .Traverse(async model =>
                            {
                                switch (model.Key.Resource)
                                {
                                    case IResourceWithDto resourceWithDto:
                                        var dtoOption = await getDtoFromApim(resourceWithDto, model.Key.Name, model.Key.Parents, cancellationToken);

                                        return dtoOption.Map(dto => model.ValidateDto(dto)
                                                                         .MapError(error => Error.From($"Validation failed for {model.Key}. {error}")))
                                                        .IfNone(() => Error.From($"Resource '{model.Key}' was not found in APIM after publishing."));
                                    default:
                                        return Unit.Instance;
                                }
                            }, cancellationToken);

            return result.Map(_ => Unit.Instance);
        }

        async ValueTask<Result<Unit>> validateOnlyExpectedResourcesExist(ImmutableArray<ITestModel> models, CancellationToken cancellationToken)
        {
            var result =
                await models.ToAsyncEnumerable()
                            .Where(async (model, cancellationToken) => await isResourceKeySupported(model.Key, cancellationToken))
                            .GroupBy(model => (model.Key.Resource, model.Key.Parents),
                                     model => model.Key.Name)
                            .SelectMany(async (group, cancellationToken) =>
                            {
                                var (resource, parents) = group.Key;
                                var modelNames = group.ToImmutableHashSet();

                                var apimNames =
                                    await listNamesFromApim(resource, parents, cancellationToken)
                                            .Where(async (name, cancellationToken) => await isResourceKeySupported(ResourceKey.From(resource, name, parents), cancellationToken))
                                            // Skip composites where the secondary resource is a non-current API revision
                                            .Where(async (name, cancellationToken) =>
                                            {
                                                if (resource is not ICompositeResource compositeResource
                                                    || compositeResource.Secondary is not (ApiResource or WorkspaceApiResource))
                                                {
                                                    return true;
                                                }

                                                var apiNameOption = compositeResource switch
                                                {
                                                    ILinkResource linkResource => from dto in await getDtoFromApim(compositeResource, name, parents, cancellationToken)
                                                                                  from apiName in ResourceModule.GetSecondaryResourceName(linkResource, dto)
                                                                                  select apiName,
                                                    _ => name
                                                };

                                                return apiNameOption.Map(ApiRevisionModule.IsRootName)
                                                                    .IfNone(() => true);
                                            })
                                            .ToArrayAsync(cancellationToken);

                                return apimNames.Where(name => modelNames.Contains(name) is false)
                                                .Select(name => ResourceKey.From(resource, name, parents));
                            })
                            .Traverse(async key => Result.Error<Unit>(Error.From($"Resource '{key}' exists in APIM but has no corresponding model.")), cancellationToken);

            return result.Map(_ => Unit.Instance);
        }
    }

    public static void ConfigureWriteGitCommit(IHostApplicationBuilder builder)
    {
        ApimModule.ConfigureIsResourceKeySupported(builder);

        builder.TryAddSingleton(ResolveWriteGitCommit);
    }

    private static WriteGitCommit ResolveWriteGitCommit(IServiceProvider provider)
    {
        var isResourceKeySupported = provider.GetRequiredService<IsResourceKeySupported>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (serviceDirectory, state, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("git.write.commit")
                                              ?.SetTag("service.directory", serviceDirectory)
                                              ?.SetTag("state", state);

            deleteNonGitArtifacts(serviceDirectory, cancellationToken);
            await writeModels(serviceDirectory, state, cancellationToken);
            return commitChanges(serviceDirectory, cancellationToken);
        };

        static bool isGitDirectory(DirectoryInfo directory) =>
            directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase);

        async ValueTask writeModels(ServiceDirectory serviceDirectory, TestState state, CancellationToken cancellationToken)
        {
            using var localProvider = getLocalDependenciesProvider(serviceDirectory);
            var writeInformationFile = localProvider.GetRequiredService<WriteInformationFile>();
            var writeApiSpecificationFile = localProvider.GetRequiredService<WriteApiSpecificationFile>();
            var writePolicyFile = localProvider.GetRequiredService<WritePolicyFile>();

            await state.Models
                       .IterTaskParallel(async model =>
                       {
                           if (await isResourceKeySupported(model.Key, cancellationToken) is false)
                           {
                               return;
                           }

                           var resource = model.Key.Resource;

                           if (resource is IResourceWithInformationFile resourceWithInfo)
                           {
                               await writeInformationFile(resourceWithInfo, model.Key.Name, model.ToDto(), model.Key.Parents, cancellationToken);
                           }

                           if (resource is IPolicyResource policyResource)
                           {
                               await writePolicyFile(policyResource, model.Key.Name, model.ToDto(), model.Key.Parents, cancellationToken);
                           }

                           if (model is ApiModel apiModel)
                           {
                               var option = from specificationText in apiModel.Specification
                                            let contents = BinaryData.FromString(specificationText)
                                            let specification = apiModel.Type switch
                                            {
                                                ApiType.OpenApi => new ApiSpecification.OpenApi
                                                {
                                                    Format = OpenApiFormat.Yaml.Instance,
                                                    Version = OpenApiVersion.V3.Instance
                                                } as ApiSpecification,
                                                ApiType.Wadl => ApiSpecification.Wadl.Instance,
                                                ApiType.Wsdl => ApiSpecification.Wsdl.Instance,
                                                ApiType.GraphQl => ApiSpecification.GraphQl.Instance,
                                                var type => throw new InvalidOperationException($"Cannot find specification for '{type}'.")
                                            }
                                            select (specification, contents);

                               await option.IterTask(async tuple =>
                               {
                                   var (specification, contents) = tuple;
                                   await writeApiSpecificationFile(model.Key, specification, contents, cancellationToken);
                               });
                           }
                       }, maxDegreeOfParallelism: Option.None, cancellationToken);
        }

        static void deleteNonGitArtifacts(ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var directory = serviceDirectory.ToDirectoryInfo();

            directory.GetChildDirectories()
                     .Where(child => isGitDirectory(child) is false)
                     .Iter(child => child.DeleteIfExists(), cancellationToken);

            directory.GetChildFiles()
                     .Where(file => file.Exists())
                     .Iter(file => file.Delete(), cancellationToken);
        }

        static ServiceProvider getLocalDependenciesProvider(ServiceDirectory serviceDirectory)
        {
            var builder = Host.CreateEmptyApplicationBuilder(new());

            builder.AddServiceDirectoryToConfiguration(serviceDirectory);
            ResourceModule.ConfigureWriteInformationFile(builder);
            ResourceModule.ConfigureWriteApiSpecificationFile(builder);
            ResourceModule.ConfigureWritePolicyFile(builder);

            return builder.Services.BuildServiceProvider();
        }

        static CommitId commitChanges(ServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var authorName = "apiops-test";
            var authorEmail = "apiops-test@test.com";
            var message = $"Commit {DateTimeOffset.UtcNow:O}";
            var directoryInfo = serviceDirectory.ToDirectoryInfo();

            var isInitialized = directoryInfo.GetChildDirectories()
                                             .Any(isGitDirectory);

            var commit = isInitialized
                            ? GitModule.CommitChanges(directoryInfo, message, authorName, authorEmail, DateTimeOffset.UtcNow)
                            : GitModule.InitializeRepository(directoryInfo, message, authorName, authorEmail, DateTimeOffset.UtcNow);

            return CommitId.From(commit);
        }
    }

    public static void ConfigureValidatePublisherStateTransition(IHostApplicationBuilder builder)
    {
        ResourceModule.ConfigureGetOptionalResourceDtoFromApim(builder);
        ResourceModule.ConfigureDoesResourceExistInApim(builder);
        ApimModule.ConfigureIsResourceKeySupported(builder);

        builder.TryAddSingleton(ResolveValidatePublisherStateTransition);
    }

    private static ValidatePublisherStateTransition ResolveValidatePublisherStateTransition(IServiceProvider provider)
    {
        var getDtoFromApim = provider.GetRequiredService<GetOptionalResourceDtoFromApim>();
        var resourceExists = provider.GetRequiredService<DoesResourceExistInApim>();
        var isResourceKeySupported = provider.GetRequiredService<IsResourceKeySupported>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (previousState, currentState, cancellationToken) =>
        {
            using var activity = activitySource.StartActivity("publisher.validate.state.transition")
                                              ?.SetTag("previous.state", previousState)
                                              ?.SetTag("current.state", currentState);

            // Models in current state should have correct DTOs in APIM
            var result =
                await currentState.Models
                                  .ToAsyncEnumerable()
                                  .Where(async (model, cancellationToken) => await isResourceKeySupported(model.Key, cancellationToken))
                                  .Traverse(async model =>
                                  {
                                      switch (model.Key.Resource)
                                      {
                                          case IResourceWithDto resourceWithDto:
                                              var dtoOption = await getDtoFromApim(resourceWithDto, model.Key.Name, model.Key.Parents, cancellationToken);

                                              return dtoOption.Map(dto => model.ValidateDto(dto)
                                                                               .MapError(error => Error.From($"Validation failed for {model.Key}. {error}")))
                                                              .IfNone(() => Error.From($"Resource '{model.Key}' was not found in APIM after commit-based publish."));
                                          default:
                                              return Unit.Instance;
                                      }
                                  }, cancellationToken);

            result.IfErrorThrow();

            // Deleted models should not exist in APIM
            var currentKeys = currentState.Models
                                          .Select(model => model.Key)
                                          .ToImmutableHashSet();

            var deletedKeys = previousState.Models
                                           .Select(model => model.Key)
                                           .Where(key => currentKeys.Contains(key) is false);

            // Don't consider an API "deleted" if its revision went from non-current to current
            var currentRevisionApis = currentState.Models
                                                  .OfType<ApiModel>()
                                                  .Where(model => model.RootName == model.Key.Name)
                                                  .ToImmutableDictionary(model => model.RootName, model => model.RevisionNumber);

            deletedKeys = deletedKeys.Where(key => key.Resource is not ApiResource
                                                   || ApiRevisionModule.Parse(key.Name)
                                                                       .Where(tuple => currentRevisionApis.TryGetValue(tuple.RootName, out var currentRevisionNumber)
                                                                                       && tuple.Revision == currentRevisionNumber)
                                                                       .IsNone);

            result =
                await deletedKeys.ToAsyncEnumerable()
                                 .Where(async (key, cancellationToken) => await isResourceKeySupported(key, cancellationToken))
                                 .Traverse(async key =>
                                 {
                                     var exists = await resourceExists(key, cancellationToken);

                                     return exists
                                         ? Result.Error<Unit>(Error.From($"Resource '{key}' should have been deleted but still exists in APIM."))
                                         : Result.Success(Unit.Instance);
                                 }, cancellationToken);

            result.IfErrorThrow();
        };
    }
}