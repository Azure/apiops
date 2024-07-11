﻿using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractProductTags(ProductName productName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(TagName Name, ProductTagDto Dto)> ListProductTags(ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductTagArtifacts(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductTagInformationFile(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken);

public static class ProductTagModule
{
    public static void ConfigureExtractProductTags(IHostApplicationBuilder builder)
    {
        ConfigureListProductTags(builder);
        TagModule.ConfigureShouldExtractTag(builder);
        ConfigureWriteProductTagArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractProductTags);
    }

    private static ExtractProductTags GetExtractProductTags(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListProductTags>();
        var shouldExtractTag = provider.GetRequiredService<ShouldExtractTag>();
        var writeArtifacts = provider.GetRequiredService<WriteProductTagArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractProductTags));

            logger.LogInformation("Extracting tags for product {ProductName}...", productName);

            await list(productName, cancellationToken)
                    .Where(tag => shouldExtractTag(tag.Name))
                    .IterParallel(async producttag => await writeArtifacts(producttag.Name, producttag.Dto, productName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListProductTags(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListProductTags);
    }

    private static ListProductTags GetListProductTags(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (gatewyName, cancellationToken) =>
            ProductTagsUri.From(gatewyName, serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteProductTagArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteProductTagInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteProductTagArtifacts);
    }

    private static WriteProductTagArtifacts GetWriteProductTagArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteProductTagInformationFile>();

        return async (name, dto, productName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, productName, cancellationToken);
        };
    }

    public static void ConfigureWriteProductTagInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteProductTagInformationFile);
    }

    private static WriteProductTagInformationFile GetWriteProductTagInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            var informationFile = ProductTagInformationFile.From(name, productName, serviceDirectory);

            logger.LogInformation("Writing product tag information file {ProductTagInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}