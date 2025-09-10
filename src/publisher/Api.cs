using Azure.Core.Pipeline;
using common;
using DotNext.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<int>> GetCurrentFileSystemApiRevision(ResourceName apiName, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask PutApi(ResourceName name, ResourceAncestors ancestors, JsonObject dto, CancellationToken cancellationToken);

internal static class ApiModule
{
    public static void ConfigureGetCurrentFileSystemApiRevision(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        CommonModule.ConfigureReadCurrentFile(builder);
        builder.TryAddSingleton(GetGetCurrentFileSystemApiRevision);
    }

    private static GetCurrentFileSystemApiRevision GetGetCurrentFileSystemApiRevision(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var readCurrentFile = provider.GetRequiredService<ReadCurrentFile>();

        var cache = new ConcurrentDictionary<(ResourceName, ResourceAncestors), AsyncLazy<Option<int>>>();

        return async (name, ancestors, cancellationToken) =>
        {
            var rootName = ApiRevisionModule.GetRootName(name);
            var resource = ApiResource.Instance;

            var lazy = cache.GetOrAdd((rootName, ancestors),
                                       _ => new(async cancellationToken =>
                                            from json in await resource.GetInformationFileDto(rootName, ancestors, serviceDirectory, readCurrentFile.Invoke, cancellationToken)
                                            from dto in JsonNodeModule.To<ApiDto>(json, ((IResourceWithDto)resource).SerializerOptions).ToOption()
                                            from revision in int.TryParse(dto.Properties.ApiRevision, out var revision) ? Option.Some(revision) : Option.None
                                            select revision));

            return await lazy.WithCancellation(cancellationToken);
        };
    }

    public static void ConfigurePutApi(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        builder.TryAddSingleton(GetPutApi);
    }

    private static PutApi GetPutApi(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        var resource = ApiResource.Instance;

        return async (name, ancestors, dto, cancellationToken) =>
        {
            var uri = resource.GetUri(name, ancestors, serviceUri);

            if (ApiRevisionModule.IsRootName(name))
            {
                await updateCurrentApiRevision(name, dto, ancestors, cancellationToken);
            }

            await resource.PutDto(name, dto, ancestors, serviceUri, pipeline, cancellationToken);
        };

        async ValueTask updateCurrentApiRevision(ResourceName name, JsonObject dto, ResourceAncestors ancestors, CancellationToken cancellationToken)
        {
            // If the API already exists...
            if (await resource.Exists(name, ancestors, serviceUri, pipeline, cancellationToken))
            {
                // Get its existing revision number
                var currentRevisionResult = from apiDtoJson in await resource.GetDto(name, ancestors, serviceUri, pipeline, cancellationToken)
                                            from revision in getApiRevision(apiDtoJson)
                                            select revision;
                var currentRevision = currentRevisionResult.IfErrorThrow();

                // Get the new revision number
                var newRevision = getApiRevision(dto).IfErrorThrow();

                // If the revision numbers are different...
                if (currentRevision != newRevision)
                {
                    logger.LogInformation("Changing the revision of {Resource} '{Name}'{Ancestors} from {PreviousRevision} to {NewRevision}...", resource.SingularName, name, ancestors.ToLogString(), currentRevision, newRevision);

                    // Create the revisioned API
                    var revisionedName = ApiRevisionModule.Combine(name, newRevision);

                    var revisionDto = JsonObjectModule.From(new ApiDto
                    {
                        Properties = new()
                        {
                            ApiRevision = newRevision.ToString(),
                            SourceApiId = ancestors.Append(resource, name).ToResourceId()
                        }
                    }).IfErrorThrow();

                    await resource.PutDto(revisionedName, revisionDto, ancestors, serviceUri, pipeline, cancellationToken);

                    // Then create a release to make it current
                    await makeApiCurrent(revisionedName, ancestors, cancellationToken);
                }
            }
        }

        async ValueTask makeApiCurrent(ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken)
        {
            var releaseName = ResourceName.From($"apiops-set-current-{Guid.NewGuid().ToString()[..8]}")
                                          .IfErrorThrow();

            var releaseAncestors = ancestors.Append(resource, name);

            var releaseDto = JsonObjectModule.From(new ApiReleaseDto
            {
                Properties = new()
                {
                    ApiId = releaseAncestors.ToResourceId(),
                    Notes = "Setting current revision for ApiOps"
                }
            }).IfErrorThrow();

            var releaseResource = ApiReleaseResource.Instance;
            await releaseResource.PutDto(releaseName, releaseDto, releaseAncestors, serviceUri, pipeline, cancellationToken);
            await releaseResource.Delete(releaseName, releaseAncestors, serviceUri, pipeline, cancellationToken);
        }

        Result<int> getApiRevision(JsonObject json) =>
            from dto in JsonNodeModule.To<ApiDto>(json, ((IResourceWithDto)resource).SerializerOptions)
            from revisionNumber in int.TryParse(dto.Properties.ApiRevision, out var number)
                                    ? Result.Success(number)
                                    : Error.From($"Could not parse revision number from JSON {json.ToJsonString()}.")
            select revisionNumber;
    }
}
