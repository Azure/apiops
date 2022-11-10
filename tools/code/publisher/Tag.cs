using common;
using Microsoft.Extensions.Logging;
using MoreLinq;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal static class Tag
{
    public static async ValueTask ProcessDeletedArtifacts(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetTagInformationFiles(files, serviceDirectory)
                .Select(GetTagName)
                .ForEachParallel(async tagName => await Delete(tagName, serviceUri, deleteRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<TagInformationFile> GetTagInformationFiles(IReadOnlyCollection<FileInfo> files, ServiceDirectory serviceDirectory)
    {
        return files.Choose(file => TryGetTagInformationFile(file, serviceDirectory));
    }

    private static TagInformationFile? TryGetTagInformationFile(FileInfo? file, ServiceDirectory serviceDirectory)
    {
        if (file is null || file.Name.Equals(TagInformationFile.Name) is false)
        {
            return null;
        }

        var tagDirectory = TryGetTagDirectory(file.Directory, serviceDirectory);

        return tagDirectory is null
                ? null
                : new TagInformationFile(tagDirectory);
    }

    private static TagDirectory? TryGetTagDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var tagsDirectory = TryGetTagsDirectory(directory.Parent, serviceDirectory);
        if (tagsDirectory is null)
        {
            return null;
        }

        var tagName = new TagName(directory.Name);
        return new TagDirectory(tagName, tagsDirectory);
    }

    private static TagsDirectory? TryGetTagsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        return directory is null
            || directory.Name.Equals(TagsDirectory.Name) is false
            || serviceDirectory.PathEquals(directory.Parent) is false
            ? null
            : new TagsDirectory(serviceDirectory);
    }

    private static TagName GetTagName(TagInformationFile file)
    {
        return new(file.TagDirectory.GetName());
    }

    private static async ValueTask Delete(TagName tagName, ServiceUri serviceUri, DeleteRestResource deleteRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = GetTagUri(tagName, serviceUri);

        logger.LogInformation("Deleting tag {tagName}...", tagName);
        await deleteRestResource(uri.Uri, cancellationToken);
    }

    private static TagUri GetTagUri(TagName tagName, ServiceUri serviceUri)
    {
        var tagsUri = new TagsUri(serviceUri);
        return new TagUri(tagName, tagsUri);
    }

    public static async ValueTask ProcessArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await GetArtifactsToPut(files, configurationJson, serviceDirectory)
                .ForEachParallel(async artifact => await PutTag(artifact.Name, artifact.Json, serviceUri, putRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IEnumerable<(TagName Name, JsonObject Json)> GetArtifactsToPut(IReadOnlyCollection<FileInfo> files, JsonObject configurationJson, ServiceDirectory serviceDirectory)
    {
        var configurationArtifacts = GetConfigurationTags(configurationJson);

        return GetTagInformationFiles(files, serviceDirectory)
                .Select(file => (Name: GetTagName(file), Json: file.ReadAsJsonObject()))
                .LeftJoin(configurationArtifacts,
                          keySelector: artifact => artifact.Name,
                          bothSelector: (fileArtifact, configurationArtifact) => (fileArtifact.Name, fileArtifact.Json.Merge(configurationArtifact.Json)));
    }

    private static IEnumerable<(TagName Name, JsonObject Json)> GetConfigurationTags(JsonObject configurationJson)
    {
        return configurationJson.TryGetJsonArrayProperty("tags")
                                .IfNullEmpty()
                                .Choose(node => node as JsonObject)
                                .Choose(jsonObject =>
                                {
                                    var name = jsonObject.TryGetStringProperty("name");
                                    return name is null
                                            ? null as (TagName, JsonObject)?
                                            : (new TagName(name), jsonObject);
                                });
    }

    private static async ValueTask PutTag(TagName tagName, JsonObject json, ServiceUri serviceUri, PutRestResource putRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting tag {tagName}...", tagName);

        var uri = GetTagUri(tagName, serviceUri);
        await putRestResource(uri.Uri, json, cancellationToken);
    }
}