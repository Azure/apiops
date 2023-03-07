﻿using common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal static class ProductGroup
{
    public static async ValueTask ExportAll(ProductDirectory productDirectory, ProductUri productUri, ListRestResources listRestResources, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var productGroupsFile = new ProductGroupsFile(productDirectory);

            var productGroups = await List(productUri, listRestResources, cancellationToken)
                                        .Select(SerializeProductGroup)
                                        .ToJsonArray(cancellationToken);

            if (productGroups.Any())
            {
                logger.LogInformation("Writing product groups file {filePath}...", productGroupsFile.Path);
                await productGroupsFile.OverwriteWithJson(productGroups, cancellationToken);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.BadRequest && exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase))
        {
            await ValueTask.CompletedTask;
        }
    }

    private static IAsyncEnumerable<GroupName> List(ProductUri productUri, ListRestResources listRestResources, CancellationToken cancellationToken)
    {
        var groupsUri = new ProductGroupsUri(productUri);
        var groupJsonObjects = listRestResources(groupsUri.Uri, cancellationToken);
        return groupJsonObjects.Select(json => json.GetStringProperty("name"))
                               .Select(name => new GroupName(name));
    }

    private static JsonObject SerializeProductGroup(GroupName groupName)
    {
        return new JsonObject
        {
            ["name"] = groupName.ToString()
        };
    }
}
