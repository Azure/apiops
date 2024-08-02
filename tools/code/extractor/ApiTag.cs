using Azure.Core.Pipeline;
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

public delegate ValueTask ExtractApiTags(ApiName apiName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(TagName Name, ApiTagDto Dto)> ListApiTags(ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiTagArtifacts(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiTagInformationFile(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiTagModule
{
    public static void ConfigureExtractApiTags(IHostApplicationBuilder builder)
    {
        ConfigureListApiTags(builder);
        ConfigureWriteApiTagArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractApiTags);
    }

    private static ExtractApiTags GetExtractApiTags(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiTags>();
        var writeArtifacts = provider.GetRequiredService<WriteApiTagArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractApiTags));

            logger.LogInformation("Extracting tags for API {ApiName}...", apiName);

            await list(apiName, cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, apiName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListApiTags(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApiTags);
    }

    private static ListApiTags GetListApiTags(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationTags = findConfigurationNamesFactory.Create<TagName>();

        return (apiName, cancellationToken) =>
        {
            var apiTagsUri = ApiTagsUri.From(apiName, serviceUri);
            var resources = apiTagsUri.List(pipeline, cancellationToken);
            return resources.Where(resource => shouldExtractTag(resource.Name));
        };

        bool shouldExtractTag(TagName name) =>
            findConfigurationTags()
                .Map(names => names.Contains(name))
                .IfNone(true);
    }

    private static void ConfigureWriteApiTagArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteApiTagInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteApiTagArtifacts);
    }

    private static WriteApiTagArtifacts GetWriteApiTagArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteApiTagInformationFile>();

        return async (name, dto, apiName, cancellationToken) =>
            await writeInformationFile(name, dto, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiTagInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteApiTagInformationFile);
    }

    private static WriteApiTagInformationFile GetWriteApiTagInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            var informationFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);

            logger.LogInformation("Writing API tag information file {ApiTagInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}