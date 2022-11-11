using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ApiTag
{
    public static async ValueTask ExportAll(ApiDirectory apiDirectory, ApiUri apiUri, ListRestResources listRestResources, ILogger logger, CancellationToken cancellationToken)
    {
        var apiTagsFile = new ApiTagsFile(apiDirectory);

        var apiTags = await List(apiUri, listRestResources, cancellationToken)
                            .Select(SerializeApiTag)
                            .ToJsonArray(cancellationToken);

        if (apiTags.Any())
        {
            logger.LogInformation("Writing API tags file {filePath}...", apiTagsFile.Path);
            await apiTagsFile.OverwriteWithJson(apiTags, cancellationToken);
        }
    }

    private static IAsyncEnumerable<TagName> List(ApiUri apiUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var tagsUri = new ApiTagsUri(apiUri);
        var tagJsonObjects = listRestResources(tagsUri.Uri, cancellationToken);
        return tagJsonObjects.Select(json => json.GetStringProperty("name"))
                             .Select(name => new TagName(name));
    }

    private static JsonObject SerializeApiTag(TagName tagName)
    {
        return new JsonObject
        {
            ["name"] = tagName.ToString()
        };
    }
}
