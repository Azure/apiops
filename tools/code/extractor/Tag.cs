using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class Tag
{
    public static async ValueTask ExportAll(ServiceDirectory serviceDirectory, ServiceUri serviceUri, ListRestResources listRestResources, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        await List(serviceUri, listRestResources, cancellationToken)
                .ForEachParallel(async tagName => await Export(serviceDirectory, serviceUri, tagName, getRestResource, logger, cancellationToken),
                                 cancellationToken);
    }

    private static IAsyncEnumerable<TagName> List(ServiceUri serviceUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var tagsUri = new TagsUri(serviceUri);
        var tagJsonObjects = listRestResources(tagsUri.Uri, cancellationToken);
        return tagJsonObjects.Select(json => json.GetStringProperty("name"))
                             .Select(name => new TagName(name));
    }

    private static async ValueTask Export(ServiceDirectory serviceDirectory, ServiceUri serviceUri, TagName tagName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var tagsDirectory = new TagsDirectory(serviceDirectory);
        var tagDirectory = new TagDirectory(tagName, tagsDirectory);

        var tagsUri = new TagsUri(serviceUri);
        var tagUri = new TagUri(tagName, tagsUri);

        await ExportInformationFile(tagDirectory, tagUri, tagName, getRestResource, logger, cancellationToken);
    }

    private static async ValueTask ExportInformationFile(TagDirectory tagDirectory, TagUri tagUri, TagName tagName, GetRestResource getRestResource, ILogger logger, CancellationToken cancellationToken)
    {
        var tagInformationFile = new TagInformationFile(tagDirectory);

        var responseJson = await getRestResource(tagUri.Uri, cancellationToken);
        var tagModel = TagModel.Deserialize(tagName, responseJson);
        var contentJson = tagModel.Serialize();

        logger.LogInformation("Writing tag information file {filePath}...", tagInformationFile.Path);
        await tagInformationFile.OverwriteWithJson(contentJson, cancellationToken);
    }
}