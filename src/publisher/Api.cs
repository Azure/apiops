using common;
using DotNext.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask PutApi(ResourceName name, JsonObject dto, CancellationToken cancellationToken);
internal delegate ValueTask DeleteApi(ResourceName name, CancellationToken cancellationToken);
internal delegate ValueTask PutWorkspaceApi(ResourceName name, ParentChain parents, JsonObject dto, CancellationToken cancellationToken);
internal delegate ValueTask DeleteWorkspaceApi(ResourceName name, ParentChain parents, CancellationToken cancellationToken);

internal static partial class ResourceModule
{
    public static void ConfigurePutApi(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetCurrentFileOperations(builder);
        common.ResourceModule.ConfigureDoesResourceExistInApim(builder);
        common.ResourceModule.ConfigureGetResourceDtoFromApim(builder);
        common.ResourceModule.ConfigurePutResourceInApim(builder);
        common.ResourceModule.ConfigureDeleteResourceFromApim(builder);
        common.ResourceModule.ConfigureGetApiSpecificationFromFile(builder);
        common.ResourceModule.ConfigurePutApiSpecificationInApim(builder);
        ConfigureGetDto(builder);

        builder.TryAddSingleton(ResolvePutApi);
    }

    internal static PutApi ResolvePutApi(IServiceProvider provider)
    {
        var getCurrentFileOperations = provider.GetRequiredService<GetCurrentFileOperations>();
        var doesResourceExistInApim = provider.GetRequiredService<DoesResourceExistInApim>();
        var getDtoFromApim = provider.GetRequiredService<GetResourceDtoFromApim>();
        var putResourceInApim = provider.GetRequiredService<PutResourceInApim>();
        var deleteResourceFromApim = provider.GetRequiredService<DeleteResourceFromApim>();
        var getSpecification = provider.GetRequiredService<GetApiSpecificationFromFile>();
        var putSpecificationInApim = provider.GetRequiredService<PutApiSpecificationInApim>();
        var logger = provider.GetRequiredService<ILogger>();

        var resource = ApiResource.Instance;
        var parents = ParentChain.Empty;

        return putApi;

        async ValueTask putApi(ResourceName name, JsonObject dto, CancellationToken cancellationToken)
        {
            if (ApiRevisionModule.IsRootName(name))
            {
                await setCurrentRevision(name, dto, parents, cancellationToken);
            }

            await putResourceInApim(resource, name, dto, parents, cancellationToken);

            await putSpecification(name, cancellationToken);
        }


        async ValueTask setCurrentRevision(ResourceName name, JsonObject dto, ParentChain parents, CancellationToken cancellationToken)
        {
            var resourceKey = ResourceKey.From(resource, name, parents);

            // If the API already exists...
            if (await doesResourceExistInApim(resourceKey, cancellationToken))
            {
                var apimRevision = await getApimRevision(name, cancellationToken);
                var newRevision = GetApiRevisionFromDto(dto);

                // If the revision numbers are different...
                if (apimRevision != newRevision)
                {
                    logger.LogInformation("Changing the revision of {ResourceKey} from {PreviousRevision} to {NewRevision}...", resourceKey, apimRevision, newRevision);

                    // Create the revisioned API
                    var revisionedName = ApiRevisionModule.Combine(name, newRevision);

                    var revisionDto = JsonObjectModule.From(new ApiDto
                    {
                        Properties = new()
                        {
                            ApiRevision = newRevision.ToString(),
                            SourceApiId = parents.Append(resource, name).ToResourceId()
                        }
                    }).IfErrorThrow();

                    await putResourceInApim(resource, revisionedName, revisionDto, parents, cancellationToken);

                    // Then create a release to make it current
                    await makeApiCurrent(revisionedName, cancellationToken);
                }
            }
        }

        async ValueTask<int> getApimRevision(ResourceName name, CancellationToken cancellationToken)
        {
            var dtoJson = await getDtoFromApim(resource, name, parents, cancellationToken);

            return GetApiRevisionFromDto(dtoJson);
        }

        async ValueTask makeApiCurrent(ResourceName name, CancellationToken cancellationToken)
        {
            var releaseName = ResourceName.From($"apiops-set-current-{Guid.NewGuid().ToString()[..8]}")
                                          .IfErrorThrow();

            var releaseParents = parents.Append(resource, name);

            var releaseDto = JsonObjectModule.From(new ApiReleaseDto
            {
                Properties = new()
                {
                    ApiId = releaseParents.ToResourceId(),
                    Notes = "Setting current revision for ApiOps"
                }
            }).IfErrorThrow();

            var releaseResource = ApiReleaseResource.Instance;
            await putResourceInApim(releaseResource, releaseName, releaseDto, releaseParents, cancellationToken);

            var releaseResourceKey = ResourceKey.From(releaseResource, releaseName, releaseParents);

            await deleteResourceFromApim(releaseResourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken);
        }

        async ValueTask putSpecification(ResourceName name, CancellationToken cancellationToken)
        {
            var resourceKey = ResourceKey.From(resource, name, parents);
            var fileOperations = getCurrentFileOperations();

            var specificationOption = await getSpecification(resourceKey, fileOperations.ReadFile, cancellationToken);

            await specificationOption.IterTask(async specification => await putSpecificationInApim(resourceKey, specification.Specification, specification.Contents, cancellationToken));
        }
    }

    private static int GetApiRevisionFromDto(JsonObject dtoJson)
    {
        var serializerOptions = ((IResourceWithDto)ApiResource.Instance).SerializerOptions;

        var result = from dto in JsonNodeModule.To<ApiDto>(dtoJson, serializerOptions)
                     from revisionNumber in int.TryParse(dto.Properties.ApiRevision, out var number)
                                             ? Result.Success(number)
                                             : Error.From($"Could not parse revision number from JSON {dtoJson.ToJsonString()}.")
                     select revisionNumber;

        return result.IfErrorThrow();
    }

    private static void ConfigureDeleteApi(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureDoesResourceExistInApim(builder);
        common.ResourceModule.ConfigureDeleteResourceFromApim(builder);
        ConfigureGetDto(builder);

        builder.TryAddSingleton(ResolveDeleteApi);
    }

    internal static DeleteApi ResolveDeleteApi(IServiceProvider provider)
    {
        var doesResourceExistInApim = provider.GetRequiredService<DoesResourceExistInApim>();
        var deleteResourceFromApim = provider.GetRequiredService<DeleteResourceFromApim>();
        var getDto = provider.GetRequiredService<GetDto>();
        var logger = provider.GetRequiredService<ILogger>();

        var currentRevisionCache = new ConcurrentDictionary<ResourceName, AsyncLazy<Option<int>>>();
        var resource = ApiResource.Instance;
        var parents = ParentChain.Empty;

        return async (name, cancellationToken) =>
        {
            if (await isCurrentApiRevision(name, cancellationToken))
            {
                logger.LogInformation("Skipping deletion of current API revision '{Name}'...", name);
                return;
            }

            var resourceKey = ResourceKey.From(resource, name, parents);

            await deleteResourceFromApim(resourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken);
        };

        // Check if the resource has a revisioned name but is the current API. This handles scenarios like this:
        // 1. Create apiA with two revisions: revision 1 and revision 2. The current revision is revision 1.
        // 2. Run the extractor. There will be two folders: apiA (revision 1, current) and apiA;rev2 (revision 2, not current).
        // 3. Make revision 2 the current revision.
        // 4. Run the extractor again. There will be two folders: apiA (revision 2, current) and apiA;rev1 (revision 1, not current).
        // 5. When we run the publisher, apiA;rev2 appear in the list of deleted folders. We don't want to delete revision 2 though, as it's now the current revision.
        async ValueTask<bool> isCurrentApiRevision(ResourceName name, CancellationToken cancellationToken)
        {
            var option = await ApiRevisionModule.Parse(name)
                                                .BindTask(async x =>
                                                {
                                                    var (_, revision) = x;

                                                    return from currentRevision in await getCurrentRevision(name, cancellationToken)
                                                           select currentRevision == revision;
                                                });

            return option.IfNone(() => false);
        }

        async ValueTask<Option<int>> getCurrentRevision(ResourceName name, CancellationToken cancellationToken) =>
            await currentRevisionCache.GetOrAdd(ApiRevisionModule.GetRootName(name), rootName => new(async cancellationToken =>
            {
                return from dto in await getDto(resource, rootName, parents, cancellationToken)
                       let revision = GetApiRevisionFromDto(dto)
                       select revision;
            })).WithCancellation(cancellationToken);
    }
    public static void ConfigurePutWorkspaceApi(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetCurrentFileOperations(builder);
        common.ResourceModule.ConfigureDoesResourceExistInApim(builder);
        common.ResourceModule.ConfigureGetResourceDtoFromApim(builder);
        common.ResourceModule.ConfigurePutResourceInApim(builder);
        common.ResourceModule.ConfigureDeleteResourceFromApim(builder);
        common.ResourceModule.ConfigureGetApiSpecificationFromFile(builder);
        common.ResourceModule.ConfigurePutApiSpecificationInApim(builder);
        ConfigureGetDto(builder);

        builder.TryAddSingleton(ResolvePutWorkspaceApi);
    }

    internal static PutWorkspaceApi ResolvePutWorkspaceApi(IServiceProvider provider)
    {
        var getCurrentFileOperations = provider.GetRequiredService<GetCurrentFileOperations>();
        var doesResourceExistInApim = provider.GetRequiredService<DoesResourceExistInApim>();
        var getDtoFromApim = provider.GetRequiredService<GetResourceDtoFromApim>();
        var putResourceInApim = provider.GetRequiredService<PutResourceInApim>();
        var deleteResourceFromApim = provider.GetRequiredService<DeleteResourceFromApim>();
        var getSpecification = provider.GetRequiredService<GetApiSpecificationFromFile>();
        var putSpecificationInApim = provider.GetRequiredService<PutApiSpecificationInApim>();
        var logger = provider.GetRequiredService<ILogger>();

        var resource = WorkspaceApiResource.Instance;

        return async (name, parents, dto, cancellationToken) =>
        {
            var resourceKey = ResourceKey.From(resource, name, parents);

            if (ApiRevisionModule.IsRootName(name))
            {
                await setCurrentRevision(resourceKey, dto, cancellationToken);
            }

            await putResourceInApim(resource, name, dto, parents, cancellationToken);

            await putSpecification(resourceKey, cancellationToken);
        };

        async ValueTask setCurrentRevision(ResourceKey resourceKey, JsonObject dto, CancellationToken cancellationToken)
        {
            // If the API already exists...
            if (await doesResourceExistInApim(resourceKey, cancellationToken))
            {
                var apimRevision = await getApimRevision(resourceKey, cancellationToken);
                var newRevision = GetWorkspaceApiRevisionFromDto(dto);

                // If the revision numbers are different...
                if (apimRevision != newRevision)
                {
                    logger.LogInformation("Changing the revision of {ResourceKey} from {PreviousRevision} to {NewRevision}...", resourceKey, apimRevision, newRevision);

                    // Create the revisioned API
                    var revisionedName = ApiRevisionModule.Combine(resourceKey.Name, newRevision);

                    var revisionDto = JsonObjectModule.From(new WorkspaceApiDto
                    {
                        Properties = new()
                        {
                            ApiRevision = newRevision.ToString(),
                            SourceApiId = resourceKey.Parents.Append(resource, resourceKey.Name).ToResourceId()
                        }
                    }).IfErrorThrow();

                    await putResourceInApim(resource, revisionedName, revisionDto, resourceKey.Parents, cancellationToken);

                    // Then create a release to make it current
                    await makeApiCurrent(resourceKey.Parents, revisionedName, cancellationToken);
                }
            }
        }

        async ValueTask<int> getApimRevision(ResourceKey resourceKey, CancellationToken cancellationToken)
        {
            var dtoJson = await getDtoFromApim(resource, resourceKey.Name, resourceKey.Parents, cancellationToken);

            return GetWorkspaceApiRevisionFromDto(dtoJson);
        }

        async ValueTask putSpecification(ResourceKey resourceKey, CancellationToken cancellationToken)
        {
            var fileOperations = getCurrentFileOperations();

            var specificationOption = await getSpecification(resourceKey, fileOperations.ReadFile, cancellationToken);

            await specificationOption.IterTask(async specification =>
                await putSpecificationInApim(resourceKey, specification.Specification, specification.Contents, cancellationToken));
        }

        async ValueTask makeApiCurrent(ParentChain parents, ResourceName revisionedName, CancellationToken cancellationToken)
        {
            var releaseName = ResourceName.From($"apiops-set-current-{Guid.NewGuid().ToString()[..8]}")
                                          .IfErrorThrow();

            var releaseParents = parents.Append(resource, revisionedName);

            var releaseDto = JsonObjectModule.From(new WorkspaceApiReleaseDto
            {
                Properties = new()
                {
                    ApiId = releaseParents.ToResourceId(),
                    Notes = "Setting current revision for ApiOps"
                }
            }).IfErrorThrow();

            var releaseResource = WorkspaceApiReleaseResource.Instance;
            await putResourceInApim(releaseResource, releaseName, releaseDto, releaseParents, cancellationToken);

            var releaseResourceKey = ResourceKey.From(releaseResource, releaseName, releaseParents);

            await deleteResourceFromApim(releaseResourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken);
        }
    }

    private static int GetWorkspaceApiRevisionFromDto(JsonObject dtoJson)
    {
        var serializerOptions = ((IResourceWithDto)WorkspaceApiResource.Instance).SerializerOptions;

        var result = from dto in JsonNodeModule.To<WorkspaceApiDto>(dtoJson, serializerOptions)
                     from revisionNumber in int.TryParse(dto.Properties.ApiRevision, out var number)
                                             ? Result.Success(number)
                                             : Error.From($"Could not parse revision number from JSON {dtoJson.ToJsonString()}.")
                     select revisionNumber;

        return result.IfErrorThrow();
    }

    public static void ConfigureDeleteWorkspaceApi(IHostApplicationBuilder builder)
    {
        common.ResourceModule.ConfigureDoesResourceExistInApim(builder);
        common.ResourceModule.ConfigureDeleteResourceFromApim(builder);
        ConfigureGetDto(builder);

        builder.TryAddSingleton(ResolveDeleteWorkspaceApi);
    }

    internal static DeleteWorkspaceApi ResolveDeleteWorkspaceApi(IServiceProvider provider)
    {
        var doesResourceExistInApim = provider.GetRequiredService<DoesResourceExistInApim>();
        var deleteResourceFromApim = provider.GetRequiredService<DeleteResourceFromApim>();
        var getDto = provider.GetRequiredService<GetDto>();
        var logger = provider.GetRequiredService<ILogger>();

        var currentRevisionCache = new ConcurrentDictionary<(ResourceName RootName, ParentChain Parents), AsyncLazy<Option<int>>>();
        var resource = WorkspaceApiResource.Instance;

        return async (name, parents, cancellationToken) =>
        {
            var resourceKey = ResourceKey.From(resource, name, parents);

            if (await isCurrentApiRevision(resourceKey, cancellationToken))
            {
                logger.LogInformation("Skipping deletion of current workspace API revision '{ResourceKey}'...", resourceKey);
                return;
            }

            await deleteResourceFromApim(resourceKey, ignoreNotFound: true, waitForCompletion: true, cancellationToken);
        };

        async ValueTask<bool> isCurrentApiRevision(ResourceKey resourceKey, CancellationToken cancellationToken)
        {
            var option = await ApiRevisionModule.Parse(resourceKey.Name)
                                                .BindTask(async x =>
                                                {
                                                    var (_, revision) = x;

                                                    return from currentRevision in await getCurrentRevision(resourceKey, cancellationToken)
                                                           select currentRevision == revision;
                                                });

            return option.IfNone(() => false);
        }

        async ValueTask<Option<int>> getCurrentRevision(ResourceKey resourceKey, CancellationToken cancellationToken)
        {
            var rootName = ApiRevisionModule.GetRootName(resourceKey.Name);
            var cacheKey = (RootName: rootName, resourceKey.Parents);

            return await currentRevisionCache.GetOrAdd(cacheKey, _ => new(async cancellationToken =>
            {
                var dtoOption = await getDto(resource, rootName, resourceKey.Parents, cancellationToken);

                return from dto in dtoOption
                       select GetWorkspaceApiRevisionFromDto(dto);
            })).WithCancellation(cancellationToken);
        }
    }
}