using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class ApiTag
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }

    private static async ValueTask Put(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactApiTags(files, configurationJson, serviceDirectory)
                .ForEachParallel(async api => await Put(api.ApiName, api.TagNames, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(ApiName ApiName, ImmutableList<TagName> TagNames)> GetArtifactApiTags(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var fileArtifacts = GetApiTagsFiles(files, serviceDirectory)
                                 .Select(file =>
                                 {
                                     var apiName = GetApiName(file);
                                     var tagNames = file.ReadAsJsonArray()
                                                        .Choose(node => node as JsonObject)
                                                        .Choose(tagJsonObject => tagJsonObject.TryGetStringProperty("name"))
                                                        .Select(name => new TagName(name))
                                                        .ToImmutableList();
                                     return (ApiName: apiName, TagNames: tagNames);
                                 });

        var configurationArtifacts = GetConfigurationApiTags(configurationJson);

        return fileArtifacts.LeftJoin(configurationArtifacts,
                                      keySelector: artifact => artifact.ApiName,
                                      bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.ApiName, configurationArtifact.TagNames));
    }

    private static IEnumerable<ApiTagsFile> GetApiTagsFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetTagsFile(file, serviceDirectory));
    }

    private static ApiTagsFile? TryGetTagsFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(ApiTagsFile.Name) is false)
        {
            return null;
        }

        var apiDirectory = Api.TryGetApiDirectory(file.Directory, serviceDirectory);

        return apiDirectory is null
                ? null
                : new ApiTagsFile(apiDirectory);
    }

    private static ApiName GetApiName(ApiTagsFile file)
    {
        return new(file.ApiDirectory.GetName());
    }

    private static IEnumerable<(ApiName ApiName, ImmutableList<TagName> TagNames)> GetConfigurationApiTags(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("apis")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose<JsonObject, (ApiName ApiName, ImmutableList<TagName> TagNames)>(apiJsonObject =>
                                {
                                    var apiNameString = apiJsonObject.TryGetStringProperty("name");
                                    if (apiNameString is null)
                                    {
                                        return default;
                                    }

                                    var apiName = new ApiName(apiNameString);

                                    var tagsJsonArray = apiJsonObject.TryGetJsonArrayProperty("tags");
                                    if (tagsJsonArray is null)
                                    {
                                        return default;
                                    }

                                    if (tagsJsonArray.Any() is false)
                                    {
                                        return (apiName, ImmutableList.Create<TagName>());
                                    }

                                    // If tags are defined in configuration but none have a 'name' property, skip this resource
                                    var tagNames = tagsJsonArray.Choose(node => node as JsonObject)
                                                                    .Choose(tagJsonObject => tagJsonObject.TryGetStringProperty("name"))
                                                                    .Select(name => new TagName(name))
                                                                    .ToImmutableList();
                                    return tagNames.Any() ? (apiName, tagNames) : default;
                                });
    }

    private static async ValueTask Put(ApiName apiName, IReadOnlyCollection<TagName> tagNames, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var apiUri = GetApiUri(apiName, serviceUri);
        var apiTagsUri = new ApiTagsUri(apiUri);

        var existingTagNames = await listRestResources(apiTagsUri.Uri, cancellationToken)
                                        .Select(tagJsonObject => tagJsonObject.GetStringProperty("name"))
                                        .Select(name => new TagName(name))
                                        .ToListAsync(cancellationToken);

        var tagNamesToPut = tagNames.Except(existingTagNames);
        var tagNamesToRemove = existingTagNames.Except(tagNames);

        await tagNamesToRemove.ForEachParallel(async tagName =>
        {
            logger.LogInformation("Removing tag {tagName} in api {apiName}...", tagName, apiName);
            await Delete(tagName, apiUri, deleteRestResource, cancellationToken);
        }, cancellationToken);

        await tagNamesToPut.ForEachParallel(async tagName =>
        {
            logger.LogInformation("Putting tag {tagName} in api {apiName}...", tagName, apiName);
            await Put(tagName, apiUri, putRestResource, cancellationToken);
        }, cancellationToken);
    }

    private static ApiUri GetApiUri(ApiName apiName, ServiceUri serviceUri)
    {
        var apisUri = new ApisUri(serviceUri);
        return new ApiUri(apiName, apisUri);
    }

    private static async ValueTask Delete(TagName tagName, ApiUri apiUri, DeleteRestResource deleteRestResource, CancellationToken cancellationToken)
    {
        var apiTagsUri = new ApiTagsUri(apiUri);
        var apiTagUri = new ApiTagUri(tagName, apiTagsUri);

        await deleteRestResource(apiTagUri.Uri, cancellationToken);
    }

    private static async ValueTask Put(TagName tagName, ApiUri apiUri, PutRestResource putRestResource, CancellationToken cancellationToken)
    {
        var apiTagsUri = new ApiTagsUri(apiUri);
        var apiTagUri = new ApiTagUri(tagName, apiTagsUri);

        await putRestResource(apiTagUri.Uri, new JsonObject(), cancellationToken);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, PutRestResource putRestResource, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await Put(files, configurationJson, serviceDirectory, serviceUri, listRestResources, putRestResource, deleteRestResource, logger, cancellationToken);
    }
}