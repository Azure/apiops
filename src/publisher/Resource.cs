using common;
using DotNext.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<ImmutableHashSet<ResourceKey>> ListResourcesToProcess(CancellationToken cancellationToken);
internal delegate ValueTask<bool> IsResourceInFileSystem(ResourceKey key, CancellationToken cancellationToken);
internal delegate ValueTask PutResource(ResourceKey resourceKey, CancellationToken cancellationToken);
internal delegate ValueTask<Option<JsonObject>> GetDto(IResourceWithDto resource, ResourceName name, ParentChain parents, CancellationToken cancellationToken);
internal delegate ValueTask DeleteResource(ResourceKey resourceKey, CancellationToken cancellationToken);

internal static partial class ResourceModule
{
    public static void ConfigureListResourcesToProcess(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureParseResourceFile(builder);
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(builder);
        GitModule.ConfigureGetCurrentCommitFileOperations(builder);
        GitModule.ConfigureGetPreviousCommitFileOperations(builder);
        FileSystemModule.ConfigureGetLocalFileOperations(builder);

        builder.TryAddSingleton(ResolveListResourcesToProcess);
    }

    internal static ListResourcesToProcess ResolveListResourcesToProcess(IServiceProvider provider)
    {
        var parseFile = provider.GetRequiredService<ParseResourceFile>();
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var listCommitFiles = provider.GetRequiredService<ListServiceDirectoryFilesModifiedByCurrentCommit>();
        var getCurrentCommitFileOperations = provider.GetRequiredService<GetCurrentCommitFileOperations>();
        var getPreviousCommitFileOperations = provider.GetRequiredService<GetPreviousCommitFileOperations>();
        var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();

        return async cancellationToken =>
        {
            var resources = new ConcurrentBag<ResourceKey>();

            if (commitIdWasPassed())
            {
                await listCommitFiles()
                        .IfNone(() => throw new InvalidOperationException("Could not get files modified by current commit."))
                        .IterTaskParallel(async kvp =>
                        {
                            var (action, files) = kvp;

                            var fileOperations = getFileOperations(action);

                            await files.IterTaskParallel(async file => await processFileResource(file, fileOperations, resources.Add, cancellationToken),
                                                         maxDegreeOfParallelism: Option.None,
                                                         cancellationToken);
                        }, maxDegreeOfParallelism: Option.None, cancellationToken);
            }
            else
            {
                var fileOperations = getLocalFileOperations();

                await fileOperations.EnumerateServiceDirectoryFiles()
                                    .IterTaskParallel(async file => await processFileResource(file, fileOperations, resources.Add, cancellationToken),
                                                      maxDegreeOfParallelism: Option.None,
                                                      cancellationToken);
            }

            return [.. resources];
        };

        FileOperations getFileOperations(GitAction action) =>
            action is GitAction.Delete
                ? getPreviousCommitFileOperations()
                    .IfNone(() => throw new InvalidOperationException("Could not get file operations for previous commit."))
                : getCurrentCommitFileOperations()
                    .IfNone(() => throw new InvalidOperationException("Could not get file operations for current commit."));

        async ValueTask processFileResource(FileInfo file, FileOperations fileOperations, Action<ResourceKey> action, CancellationToken cancellationToken)
        {
            var resourceOption = await parseFile(file, fileOperations.ReadFile, cancellationToken);
            resourceOption.Iter(action);
        }
    }

    public static void ConfigureIsResourceInFileSystem(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureGetCurrentCommitFileOperations(builder);
        FileSystemModule.ConfigureGetLocalFileOperations(builder);
        common.ResourceModule.ConfigureGetInformationFileDto(builder);
        common.ResourceModule.ConfigureGetPolicyFileContents(builder);
        common.ResourceModule.ConfigureGetApiSpecificationFromFile(builder);

        builder.TryAddSingleton(ResolveIsResourceInFileSystem);
    }

    internal static IsResourceInFileSystem ResolveIsResourceInFileSystem(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getCurrentCommitFileOperations = provider.GetRequiredService<GetCurrentCommitFileOperations>();
        var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();
        var getInformationFileDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicy = provider.GetRequiredService<GetPolicyFileContents>();
        var getApiSpecification = provider.GetRequiredService<GetApiSpecificationFromFile>();

        var cache = new ConcurrentDictionary<ResourceKey, AsyncLazy<bool>>();

        return async (key, cancellationToken) =>
        {
            return await cache.GetOrAdd(key, _ => new(isInFileSystem))
                              .WithCancellation(cancellationToken);

            async Task<bool> isInFileSystem(CancellationToken cancellationToken)
            {
                var fileOperations = commitIdWasPassed()
                                     ? getCurrentCommitFileOperations()
                                           .IfNone(() => throw new InvalidOperationException("Could not get file operations for current commit."))
                                     : getLocalFileOperations();

                var readFile = fileOperations.ReadFile;
                var getSubDirectories = fileOperations.GetSubDirectories;

                // Parse the resource's information file
                if (key.Resource is IResourceWithInformationFile resourceWithInformationFile
                    && await getInformationFileDto(resourceWithInformationFile, key.Name, key.Parents, readFile.Invoke, getSubDirectories, cancellationToken) is { IsSome: true })
                {
                    return true;
                }

                // Parse the resource's policy file
                if (key.Resource is IPolicyResource policyResource
                    && await getPolicy(policyResource, key.Name, key.Parents, readFile.Invoke, cancellationToken) is { IsSome: true })
                {
                    return true;
                }

                // Parse the resource's API specification file
                if (key.Resource is ApiResource && await specificationExists(key, readFile.Invoke, cancellationToken))
                {
                    return true;
                }

                // Parse the resource's workspace API specification file
                if (key.Resource is WorkspaceApiResource && await specificationExists(key, readFile.Invoke, cancellationToken))
                {
                    return true;
                }

                return false;
            }
        };

        async ValueTask<bool> specificationExists(ResourceKey resourceKey, ReadFile readFile, CancellationToken cancellationToken)
        {
            var specificationOption = await getApiSpecification(resourceKey, readFile, cancellationToken);
            return specificationOption.IsSome;
        }
    }

    public static void ConfigurePutResource(IHostApplicationBuilder builder)
    {
        ConfigureGetDto(builder);
        ConfigurePutApi(builder);
        ConfigurePutWorkspaceApi(builder);
        CommonModule.ConfigureIsDryRun(builder);

        common.ResourceModule.ConfigurePutResourceInApim(builder);

        builder.TryAddSingleton(ResolvePutResource);
    }

    internal static PutResource ResolvePutResource(IServiceProvider provider)
    {
        var getDto = provider.GetRequiredService<GetDto>();
        var putApi = provider.GetRequiredService<PutApi>();
        var putWorkspaceApi = provider.GetRequiredService<PutWorkspaceApi>();
        var putResourceInApim = provider.GetRequiredService<PutResourceInApim>();
        var isDryRun = provider.GetRequiredService<IsDryRun>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resourceKey, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("put.resource")
                                       ?.SetTag("resourceKey", resourceKey);

            // Skip non-DTO resources
            if (resourceKey.Resource is not IResourceWithDto resourceWithDto)
            {
                logger.LogWarning("Cannot put {ResourceKey} as it is not a resource with DTO. Skipping it...", resourceKey);

                return;
            }

            var (name, parents) = (resourceKey.Name, resourceKey.Parents);
            var dtoOption = await getDto(resourceWithDto, name, parents, cancellationToken);

            // If we have a DTO, put it; otherwise, log and skip it.
            await dtoOption.Match(async dto =>
                                  {
                                      logger.LogInformation("Putting {ResourceKey}...", resourceKey);

                                      if (isDryRun())
                                      {
                                          return;
                                      }

                                      await putDtoResource(resourceWithDto, name, dto, parents, cancellationToken);
                                  },
                                  async () =>
                                  {
                                      logger.LogWarning("No DTO found for {ResourceKey}. Skipping it...", resourceKey);

                                      await ValueTask.CompletedTask;
                                  });
        };

        async ValueTask putDtoResource(IResourceWithDto resource, ResourceName name, JsonObject dto, ParentChain parents, CancellationToken cancellationToken) =>
            await (resource switch
            {
                ApiResource => putApi(name, dto, cancellationToken),
                WorkspaceApiResource => putWorkspaceApi(name, parents, dto, cancellationToken),
                _ => putResourceInApim(resource, name, dto, parents, cancellationToken)
            });
    }

    private static void ConfigureGetDto(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureGetCurrentCommitFileOperations(builder);
        FileSystemModule.ConfigureGetLocalFileOperations(builder);
        common.ResourceModule.ConfigureGetInformationFileDto(builder);
        common.ResourceModule.ConfigureGetPolicyFileContents(builder);
        ConfigureGetPolicyFragmentDto(builder);
        ConfigureGetWorkspacePolicyFragmentDto(builder);
        ConfigurationModule.ConfigureGetConfigurationOverride(builder);

        builder.TryAddSingleton(ResolveGetDto);
    }

    internal static GetDto ResolveGetDto(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var getCurrentCommitFileOperations = provider.GetRequiredService<GetCurrentCommitFileOperations>();
        var getLocalFileOperations = provider.GetRequiredService<GetLocalFileOperations>();
        var getInformationFileDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();
        var getPolicyFragmentDto = provider.GetRequiredService<GetPolicyFragmentDto>();
        var getWorkspacePolicyFragmentDto = provider.GetRequiredService<GetWorkspacePolicyFragmentDto>();
        var getConfigurationOverride = provider.GetRequiredService<GetConfigurationOverride>();
        var logger = provider.GetRequiredService<ILogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (resource, name, parents, cancellationToken) =>
        {
            var resourceKey = new ResourceKey
            {
                Resource = resource,
                Name = name,
                Parents = parents
            };

            using var _ = activitySource.StartActivity("get.dto")
                                       ?.SetTag("resourceKey", resourceKey);

            var dtoOption = await getDtoFromFileSystem(resource, name, parents, cancellationToken);

            dtoOption = await mergeDtoWithConfigurationOverride(resourceKey, dtoOption, cancellationToken);

            dtoOption = validateNamedValue(resourceKey, dtoOption);
            dtoOption = validateWorkspaceNamedValue(resourceKey, dtoOption);

            return dtoOption;
        };

        async ValueTask<Option<JsonObject>> getDtoFromFileSystem(IResourceWithDto resource, ResourceName name, ParentChain parents, CancellationToken cancellationToken)
        {
            var fileOperations = getFileOperations();
            var readFile = fileOperations.ReadFile;
            var getSubDirectories = fileOperations.GetSubDirectories;

            switch (resource)
            {
                case PolicyFragmentResource:
                    return await getPolicyFragmentDto(name, cancellationToken);
                case WorkspacePolicyFragmentResource:
                    return await getWorkspacePolicyFragmentDto(name, parents, cancellationToken);
                case IResourceWithInformationFile resourceWithInformationFile:
                    return await getInformationFileDto(resourceWithInformationFile, name, parents, readFile, getSubDirectories, cancellationToken);
                case IPolicyResource policyResource:
                    var policyContentsOption = await getPolicyFileContents(policyResource, name, parents, readFile, cancellationToken);
                    return policyContentsOption.Map(PolicyContentsToDto);
                default:
                    return Option.None;
            }
        }

        FileOperations getFileOperations() =>
            commitIdWasPassed()
                ? getCurrentCommitFileOperations()
                    .IfNone(() => throw new InvalidOperationException("Could not get file operations for current commit."))
                : getLocalFileOperations();

        async ValueTask<Option<JsonObject>> mergeDtoWithConfigurationOverride(ResourceKey resourceKey, Option<JsonObject> dtoOption, CancellationToken cancellationToken) =>
            await dtoOption.MapTask(async dto =>
            {
                var option = from configurationOverride in await getConfigurationOverride(resourceKey, cancellationToken)
                             select dto.MergeWith(configurationOverride);

                return option.IfNone(() => dto);
            });

        Option<JsonObject> validateNamedValue(ResourceKey resourceKey, Option<JsonObject> dtoOption) =>
            dtoOption.Bind(json =>
            {
                // Don't put secret named values without a value or Key Vault identifier
                if (resourceKey.Resource is NamedValueResource
                    && json.Deserialize<NamedValueDto>() is NamedValueDto namedValueDto
                    && namedValueDto.Properties.Secret is true
                    && namedValueDto.Properties.Value is null
                    && namedValueDto.Properties.KeyVault?.SecretIdentifier is null)
                {
                    logger.LogWarning("Named value '{ResourceKey}' is secret but has no value or key vault identifier. Skipping it...", resourceKey);
                    return Option<JsonObject>.None();
                }
                else
                {
                    return json;
                }
            });

        Option<JsonObject> validateWorkspaceNamedValue(ResourceKey resourceKey, Option<JsonObject> dtoOption) =>
            dtoOption.Bind(json =>
            {
                // Don't put secret named values without a value or Key Vault identifier
                if (resourceKey.Resource is WorkspaceNamedValueResource
                    && json.Deserialize<WorkspaceNamedValueDto>() is WorkspaceNamedValueDto namedValueDto
                    && namedValueDto.Properties.Secret is true
                    && namedValueDto.Properties.Value is null
                    && namedValueDto.Properties.KeyVault?.SecretIdentifier is null)
                {
                    logger.LogWarning("Named value '{ResourceKey}' is secret but has no value or key vault identifier. Skipping it...", resourceKey);
                    return Option<JsonObject>.None();
                }
                else
                {
                    return json;
                }
            });
    }

    private static JsonObject PolicyContentsToDto(BinaryData contents) =>
        new()
        {
            ["properties"] = new JsonObject
            {
                ["format"] = "rawxml",
                ["value"] = contents.ToString()
            }
        };

    public static void ConfigureDeleteResource(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureDeleteResourceFromApim(builder);
        ConfigureDeleteApi(builder);
        ConfigureDeleteWorkspaceApi(builder);
        CommonModule.ConfigureIsDryRun(builder);

        builder.TryAddSingleton(ResolveDeleteResource);
    }

    internal static DeleteResource ResolveDeleteResource(IServiceProvider provider)
    {
        var deleteResourceFromApim = provider.GetRequiredService<DeleteResourceFromApim>();
        var deleteApi = provider.GetRequiredService<DeleteApi>();
        var deleteWorkspaceApi = provider.GetRequiredService<DeleteWorkspaceApi>();
        var isDryRun = provider.GetRequiredService<IsDryRun>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resourceKey, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("delete.resource")
                                       ?.SetTag("resourceKey", resourceKey);

            logger.LogInformation("Deleting {ResourceKey}...", resourceKey);

            if (isDryRun())
            {
                return;
            }

            await (resourceKey.Resource switch
            {
                ApiResource => deleteApi(resourceKey.Name, cancellationToken),
                WorkspaceApiResource => deleteWorkspaceApi(resourceKey.Name, resourceKey.Parents, cancellationToken),
                _ => deleteResourceFromApim(resourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken)
            });
        };
    }
}
