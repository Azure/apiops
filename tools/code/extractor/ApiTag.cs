using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApiTags(ApiName apiName, CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<(TagName Name, ApiTagDto Dto)> ListApiTags(ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiTagArtifacts(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiTagInformationFile(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiTagServices
{
    public static void ConfigureExtractApiTags(IServiceCollection services)
    {
        ConfigureListApiTags(services);
        TagServices.ConfigureShouldExtractTag(services);
        ConfigureWriteApiTagArtifacts(services);

        services.TryAddSingleton(ExtractApiTags);
    }

    private static ExtractApiTags ExtractApiTags(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiTags>();
        var shouldExtractTag = provider.GetRequiredService<ShouldExtractTag>();
        var writeArtifacts = provider.GetRequiredService<WriteApiTagArtifacts>();

        return async (apiName, cancellationToken) =>
            await list(apiName, cancellationToken)
                    .Where(tag => shouldExtractTag(tag.Name))
                    .IterParallel(async tag => await writeArtifacts(tag.Name, tag.Dto, apiName, cancellationToken),
                                  cancellationToken);
    }

    private static void ConfigureListApiTags(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListApiTags);
    }

    private static ListApiTags ListApiTags(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (apiName, cancellationToken) =>
            ApiTagsUri.From(apiName, serviceUri)
                      .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteApiTagArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiTagInformationFile(services);

        services.TryAddSingleton(WriteApiTagArtifacts);
    }

    private static WriteApiTagArtifacts WriteApiTagArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteApiTagInformationFile>();

        return async (name, dto, apiName, cancellationToken) =>
            await writeInformationFile(name, dto, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiTagInformationFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteApiTagInformationFile);
    }

    private static WriteApiTagInformationFile WriteApiTagInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, dto, apiName, cancellationToken) =>
        {
            var informationFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);

            logger.LogInformation("Writing API tag information file {InformationFile}", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiTagExtractor");
}