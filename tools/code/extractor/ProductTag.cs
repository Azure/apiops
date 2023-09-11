using common;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ProductTag
{
    public static async ValueTask ExportAll(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, ILogger logger, CancellationToken cancellationToken)
    {
        var productTagsFile = new ProductTagsFile(productDirectory);

        var productTags = await List(productUri, listRestResources, cancellationToken)
                                    .Select(SerializeProductTag)
                                    .ToJsonArray(cancellationToken);

        if (productTags.Any())
        {
            logger.LogInformation("Writing product tags file {filePath}...", productTagsFile.Path);
            await productTagsFile.OverwriteWithJson(productTags, cancellationToken);
        }
    }

    private static IAsyncEnumerable<TagName> List(ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var tagsUri = new ProductTagsUri(productUri);
        var tagJsonObjects = listRestResources(tagsUri.Uri, cancellationToken);
        return tagJsonObjects.Select(json => json.GetStringProperty("name"))
                             .Select(name => new TagName(name));
    }

    private static JsonObject SerializeProductTag(TagName tagName)
    {
        return new JsonObject
        {
            ["name"] = tagName.ToString()
        };
    }
}
