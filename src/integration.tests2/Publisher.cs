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
        var parentDictionary = Updates.GroupBy(kvp => kvp.Key.Parents,
                                               kvp => kvp.Value)
                                      .ToImmutableDictionary(group => group.Key,
                                                             group => group.GroupBy(model => model.Key.Resource)
                                                                           .ToImmutableDictionary(group => group.Key,
                                                                                                  group => group.ToImmutableHashSet()));

        var rootResources = parentDictionary.Find(ParentChain.Empty)
                                            .IfNone(() => []);

        return [.. getResourceKvps(ParentChain.Empty, rootResources)];

        IEnumerable<KeyValuePair<string, JsonNode?>> getResourceKvps(ParentChain parentChain, ImmutableDictionary<IResource, ImmutableHashSet<ITestModel>> resources) =>
            from kvp in resources
            let jsonKey = kvp.Key.ConfigurationKey
            let jsonValue = kvp.Value.Select(model =>
            {
                var (resource, name) = (model.Key.Resource, model.Key.Name);
                var parentChainWithResource = parentChain.Append(resource, name);

                // Create DTO Json
                var json = model.ToDto();

                // Add name property
                json["name"] = name.ToString();

                // Add children
                parentDictionary.Find(parentChainWithResource)
                                .Iter(children => getResourceKvps(parentChainWithResource, children)
                                                    .Iter(kvp => json[kvp.Key] = kvp.Value));

                return json;
            }).ToJsonArray()
            select KeyValuePair.Create(jsonKey, jsonValue as JsonNode);
    }

    public static Gen<PublisherOverride> Generate(IEnumerable<ITestModel> models) =>
        from subsets in Generator.Traverse(TestsModule.ResourceModels.Values,
                                           type =>
                                           {
                                               var method = typeof(PublisherOverride).GetMethod(nameof(GenerateUpdatedSubSetOf),
                                                                                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                                                             ?? throw new InvalidOperationException($"Failed to find method '{nameof(GenerateUpdatedSubSetOf)}' on type '{typeof(PublisherOverride)}'.");

                                               var genericMethod = method.MakeGenericMethod(type) ?? throw new InvalidOperationException($"Failed to construct generic method for type '{type}'.");

                                               var subsetGen = genericMethod.Invoke(obj: default, parameters: [models])
                                                               ?? throw new InvalidOperationException($"Failed to invoke method '{method.Name}' for type '{type}'.");

                                               return (Gen<ImmutableHashSet<ITestModel>>)subsetGen;
                                           })
        select new PublisherOverride
        {
            Updates = subsets.SelectMany(models => models)
                             .ToImmutableDictionary(model => model.Key)
        };

    private static Gen<ImmutableHashSet<ITestModel>> GenerateUpdatedSubSetOf<T>(IEnumerable<ITestModel> models) where T : ITestModel<T> =>
        from updated in T.GenerateUpdates(models.OfType<T>())
        from subset in Generator.SubSetOf(updated)
        select subset.Cast<ITestModel>()
                     .ToImmutableHashSet();

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

                                        return dtoOption.Map(model.ValidateDto)
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
        builder.TryAddSingleton(ResolveWriteGitCommit);
    }

    private static WriteGitCommit ResolveWriteGitCommit(IServiceProvider provider)
    {
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

        static async ValueTask writeModels(ServiceDirectory serviceDirectory, TestState state, CancellationToken cancellationToken)
        {
            using var localProvider = getLocalDependenciesProvider(serviceDirectory);
            var writeInformationFile = localProvider.GetRequiredService<WriteInformationFile>();

            await state.Models
                       .IterTaskParallel(async model =>
                       {
                           switch (model.Key.Resource)
                           {
                               case IResourceWithInformationFile resourceWithInfo:
                                   await writeInformationFile(resourceWithInfo, model.Key.Name, model.ToDto(), model.Key.Parents, cancellationToken);
                                   break;
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

                                              return dtoOption.Map(model.ValidateDto)
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