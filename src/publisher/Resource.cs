using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors)>> ParseFile(FileInfo file, ReadFile readFile, CancellationToken cancellationToken);
internal delegate IAsyncEnumerable<(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors)> ListResourcesToPut(CancellationToken cancellationToken);
internal delegate IAsyncEnumerable<(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors)> ListResourcesToDelete(CancellationToken cancellationToken);
internal delegate ValueTask PutResource(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask<Option<JsonObject>> GetDto(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken);
internal delegate ValueTask DeleteResource(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken);

internal static class ResourceModule
{
    public static void ConfigureParseFile(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        ResourceGraphModule.ConfigureBuilder(builder);

        builder.TryAddSingleton(GetParseFile);
    }

    private static ParseFile GetParseFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var graph = provider.GetRequiredService<ResourceGraph>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        var potentialResources = graph.TopologicallySortedResources
                                      .Choose(resource => resource is IResourceWithDto resourceWithDto
                                                            ? Option.Some(resourceWithDto)
                                                            : Option.None)
                                      .ToImmutableArray();

        return async (file, readFile, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("parse.file")
                                       ?.AddTag("file", file.FullName);

            var matches = await potentialResources.Choose(async resource =>
            {
                var option = Option<(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors)>.None();

                if (resource is ILinkResource linkResource)
                {
                    option = from x in await linkResource.ParseInformationFile(file, serviceDirectory, readFile, cancellationToken)
                             select (resource, x.Name, x.Ancestors);

                    if (option.IsSome)
                    {
                        return option;
                    }
                }

                if (resource is IResourceWithInformationFile resourceWithInformationFile and not ILinkResource)
                {
                    option = from x in resourceWithInformationFile.ParseInformationFile(file, serviceDirectory)
                             select (resource, x.Name, x.Ancestors);

                    if (option.IsSome)
                    {
                        return option;
                    }
                }

                if (resource is IPolicyResource policyResource)
                {
                    option = from x in policyResource.ParsePolicyFile(file, serviceDirectory)
                             select (resource, x.Name, x.Ancestors);

                    if (option.IsSome)
                    {
                        return option;
                    }
                }

                return Option.None;
            }).ToArrayAsync(cancellationToken);

            switch (matches)
            {
                case []:
                    return Option.None;
                case [var match]:
                    return match;
                default:
                    var matchesAsString = string.Join(", ", matches.Select(match => $"{match.resource.SingularName} {match.name}"));
                    throw new InvalidOperationException($"Multiple resources matched the file '{file.FullName}': {matchesAsString}.");
            }
        };
    }

    public static void ConfigureListResourcesToPut(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(builder);
        FileSystemModule.ConfigureListLocalServiceDirectoryFiles(builder);
        CommonModule.ConfigureReadCurrentFile(builder);
        ConfigureParseFile(builder);

        builder.TryAddSingleton(GetListResourcesToPut);
    }

    private static ListResourcesToPut GetListResourcesToPut(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var listFilesModifiedByCommit = provider.GetRequiredService<ListServiceDirectoryFilesModifiedByCurrentCommit>();
        var listServiceDirectoryFiles = provider.GetRequiredService<ListLocalServiceDirectoryFiles>();
        var readCurrentFile = provider.GetRequiredService<ReadCurrentFile>();
        var parseFile = provider.GetRequiredService<ParseFile>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return cancellationToken =>
        {
            using var _ = activitySource.StartActivity("list.resources.to.put");

            IEnumerable<FileInfo> files = [];

            if (commitIdWasPassed())
            {
                var option = from fileDictionary in listFilesModifiedByCommit()
                             select from kvp in fileDictionary
                                    where kvp.Key != GitAction.Delete
                                    from file in kvp.Value
                                    select file;

                files = option.IfNoneThrow(() => new InvalidOperationException("No commit ID was passed."));
            }
            else
            {
                files = listServiceDirectoryFiles();
            }

            return files.ToAsyncEnumerable()
                        .Choose(async file => await parseFile(file, readCurrentFile.Invoke, cancellationToken))
                        .SelectMany(x => (x.resource switch
                        {
                            // If this is a revisioned API, also include the root API
                            ApiResource when ApiRevisionModule.IsRootName(x.name) is false =>
                                new[] { x, (x.resource, ApiRevisionModule.GetRootName(x.name), x.ancestors) },
                            _ => [x]
                        }).ToAsyncEnumerable())
                        .Distinct();
        };
    }

    public static void ConfigureListResourcesToDelete(IHostApplicationBuilder builder)
    {
        GitModule.ConfigureCommitIdWasPassed(builder);
        GitModule.ConfigureListServiceDirectoryFilesModifiedByCurrentCommit(builder);
        GitModule.ConfigureReadPreviousCommitFile(builder);
        GitModule.ConfigureReadCurrentCommitFile(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        ApiModule.ConfigureGetCurrentFileSystemApiRevision(builder);
        ConfigureParseFile(builder);

        builder.TryAddSingleton(GetListResourcesToDelete);
    }

    private static ListResourcesToDelete GetListResourcesToDelete(IServiceProvider provider)
    {
        var commitIdWasPassed = provider.GetRequiredService<CommitIdWasPassed>();
        var listFilesModifiedByCommit = provider.GetRequiredService<ListServiceDirectoryFilesModifiedByCurrentCommit>();
        var readPreviousCommitFile = provider.GetRequiredService<ReadPreviousCommitFile>();
        var readCurrentCommitFile = provider.GetRequiredService<ReadCurrentCommitFile>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var parseFile = provider.GetRequiredService<ParseFile>();
        var getCurrentFileSystemApiRevision = provider.GetRequiredService<GetCurrentFileSystemApiRevision>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return cancellationToken =>
        {
            using var _ = activitySource.StartActivity("list.resources.to.delete");

            if (commitIdWasPassed())
            {
                var fileDictionary = listFilesModifiedByCommit()
                                         .IfNoneThrow(() => new InvalidOperationException("No commit ID was passed."));

                return fileDictionary.Where(kvp => kvp.Key == GitAction.Delete)
                                     .SelectMany(kvp => kvp.Value)
                                     .Choose(async file => await parseFile(file, readPreviousCommitFile.Invoke, cancellationToken))
                                     .Where(async (x, cancellationToken) => await isCurrentApiRevision(x.resource, x.name, x.ancestors, cancellationToken) is false)
                                     .Distinct();
            }
            else
            {
                return AsyncEnumerable.Empty<(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors)>();
            }
        };

        // Check if the resource is a current API revision with a revisioned name. This handles scenarios like this:
        // 1. Create apiA with two revisions: revision 1 and revision 2. The current revision is revision 1.
        // 2. Run the extractor. There will be two folders: apiA (revision 1, current) and apiA;rev2 (revision 2, not current).
        // 3. Make revision 2 the current revision.
        // 4. Run the extractor again. There will be two folders: apiA (revision 2, current) and apiA;rev1 (revision 1, not current).
        // 5. When we run the publisher, apiA;rev2 appear in the list of deleted folders. We don't want to delete it though, as it's now the current revision.
        async ValueTask<bool> isCurrentApiRevision(IResourceWithDto resource, ResourceName name, ResourceAncestors ancestors, CancellationToken cancellationToken) =>
            resource is ApiResource
            && await ApiRevisionModule.Parse(name)
                                      .Match(async x =>
                                             {
                                                 var (_, revisionNumber) = x;

                                                 var option = from currentRevision in await getCurrentFileSystemApiRevision(name, ancestors, cancellationToken)
                                                              select currentRevision == revisionNumber;

                                                 return option.IfNone(() => false);
                                             },
                                             async () => await ValueTask.FromResult(false));
    }

    public static void ConfigurePutResource(IHostApplicationBuilder builder)
    {
        ConfigureGetDto(builder);
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);
        ApiModule.ConfigurePutApi(builder);
        ProductApiModule.ConfigurePutProductApi(builder);

        builder.TryAddSingleton(GetPutResource);
    }

    private static PutResource GetPutResource(IServiceProvider provider)
    {
        var getDto = provider.GetRequiredService<GetDto>();
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var putApi = provider.GetRequiredService<PutApi>();
        var putProductApi = provider.GetRequiredService<PutProductApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resource, name, ancestors, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("put.resource")
                                       ?.SetTag("resource", resource.SingularName)
                                       ?.SetTag("name", name.ToString())
                                       ?.TagAncestors(ancestors);

            logger.LogInformation("Putting {Resource} '{Name}'{Ancestors}...", resource.SingularName, name, ancestors.ToLogString());

            var dtoOption = await getDto(resource, name, ancestors, cancellationToken);

            await dtoOption.IterTask(async dto => await putDto(resource, name, dto, ancestors, cancellationToken));
        };

        async ValueTask putDto(IResourceWithDto resource, ResourceName name, JsonObject dto, ResourceAncestors ancestors, CancellationToken cancellationToken) =>
            await (resource switch
            {
                ApiResource => putApi(name, ancestors, dto, cancellationToken),
                ProductApiResource => putProductApi(name, ancestors, dto, cancellationToken),
                _ => resource.PutDto(name, dto, ancestors, serviceUri, pipeline, cancellationToken)
            });
    }

    private static void ConfigureGetDto(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureReadCurrentFile(builder);
        CommonModule.ConfigureGetSubDirectories(builder);
        ManagementServiceModule.ConfigureServiceDirectory(builder);
        ConfigurationModule.ConfigureGetConfigurationOverride(builder);

        builder.TryAddSingleton(GetGetDto);
    }

    private static GetDto GetGetDto(IServiceProvider provider)
    {
        var readCurrentFile = provider.GetRequiredService<ReadCurrentFile>();
        var getSubDirectories = provider.GetRequiredService<GetSubDirectories>();
        var serviceDirectory = provider.GetRequiredService<ServiceDirectory>();
        var getConfigurationOverride = provider.GetRequiredService<GetConfigurationOverride>();
        var logger = provider.GetRequiredService<ILogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (resource, name, ancestors, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("get.dto")
                                       ?.SetTag("resource", resource.SingularName)
                                       ?.SetTag("name", name.ToString())
                                       ?.TagAncestors(ancestors);

            var dtoOption = Option<JsonObject>.None();

            switch (resource)
            {
                case PolicyFragmentResource policyFragmentResource:
                    var informationFileOption = await policyFragmentResource.GetInformationFileDto(name, ancestors, serviceDirectory, readCurrentFile.Invoke, cancellationToken);
                    var policyFileOption = await policyFragmentResource.GetPolicyFileDto(name, ancestors, serviceDirectory, readCurrentFile.Invoke, cancellationToken);

                    dtoOption = (informationFileOption.IfNoneNull(), policyFileOption.IfNoneNull()) switch
                    {
                        (null, null) => dtoOption,
                        (JsonObject informationFile, null) => informationFile,
                        (null, JsonObject policyFile) => policyFile,
                        (JsonObject informationFile, JsonObject policyFile) => informationFile.MergeWith(policyFile)
                    };

                    break;
                case ILinkResource linkResource:
                    dtoOption = await linkResource.GetInformationFileDto(name, ancestors, serviceDirectory, getSubDirectories.Invoke, readCurrentFile.Invoke, cancellationToken);
                    break;
                case IResourceWithInformationFile resourceWithInformationFile:
                    dtoOption = await resourceWithInformationFile.GetInformationFileDto(name, ancestors, serviceDirectory, readCurrentFile.Invoke, cancellationToken);
                    break;
                case IPolicyResource policyResource:
                    dtoOption = await policyResource.GetPolicyFileDto(name, ancestors, serviceDirectory, readCurrentFile.Invoke, cancellationToken);
                    break;
                default:
                    break;
            }

            // Add the configuration override if it exists
            await dtoOption.IterTask(async json =>
            {
                var overrideOption = await getConfigurationOverride(resource, name, ancestors, cancellationToken);
                overrideOption.Iter(overrideJson => dtoOption = json.MergeWith(overrideJson));
            });

            var json = dtoOption.IfNoneThrow(() => new InvalidOperationException($"Failed to get DTO for {resource.SingularName} '{name}'{ancestors.ToLogString()}."));

            // Don't put secret named values without a value or Key Vault identifier
            if (resource is NamedValueResource
                && json.Deserialize<NamedValueDto>() is NamedValueDto namedValueDto
                && namedValueDto.Properties.Secret is true
                && namedValueDto.Properties.Value is null
                && namedValueDto.Properties.KeyVault?.SecretIdentifier is null)
            {
                logger.LogWarning("Named value '{Name}' is secret but has no value or key vault identifier. Skipping it...", name);
                return Option.None;
            }

            return json;
        };
    }

    public static void ConfigureDeleteResource(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.TryAddSingleton(GetDeleteResource);
    }

    private static DeleteResource GetDeleteResource(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (resource, name, ancestors, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("delete.resource")
                                       ?.SetTag("resource", resource.SingularName)
                                       ?.SetTag("name", name.ToString())
                                       ?.TagAncestors(ancestors);

            logger.LogInformation("Deleting {Resource} '{Name}'{Ancestors}...", resource.SingularName, name, ancestors.ToLogString());

            await resource.Delete(name, ancestors, serviceUri, pipeline, cancellationToken);
        };
    }
}
