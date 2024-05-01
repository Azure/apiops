using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApiTags(ApiName apiName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(TagName Name, ApiTagDto Dto)> ListApiTags(ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask WriteApiTagArtifacts(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);

file delegate ValueTask WriteApiTagInformationFile(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);

file sealed class ExtractApiTagsHandler(ListApiTags list, WriteApiTagArtifacts writeArtifacts)
{
    public async ValueTask Handle(ApiName apiName, CancellationToken cancellationToken) =>
        await list(apiName, cancellationToken)
                .IterParallel(async apitag => await writeArtifacts(apitag.Name, apitag.Dto, apiName, cancellationToken),
                              cancellationToken);
}

file sealed class ListApiTagsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(TagName, ApiTagDto)> Handle(ApiName apiName, CancellationToken cancellationToken) =>
        ApiTagsUri.From(apiName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteApiTagArtifactsHandler(WriteApiTagInformationFile writeTagFile)
{
    public async ValueTask Handle(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        await writeTagFile(name, dto, apiName, cancellationToken);
    }
}

file sealed class WriteApiTagInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        var informationFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);

        logger.LogInformation("Writing API tag information file {ApiTagInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class ApiTagServices
{
    public static void ConfigureExtractApiTags(IServiceCollection services)
    {
        ConfigureListApiTags(services);
        ConfigureWriteApiTagArtifacts(services);

        services.TryAddSingleton<ExtractApiTagsHandler>();
        services.TryAddSingleton<ExtractApiTags>(provider => provider.GetRequiredService<ExtractApiTagsHandler>().Handle);
    }

    private static void ConfigureListApiTags(IServiceCollection services)
    {
        services.TryAddSingleton<ListApiTagsHandler>();
        services.TryAddSingleton<ListApiTags>(provider => provider.GetRequiredService<ListApiTagsHandler>().Handle);
    }

    private static void ConfigureWriteApiTagArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiTagInformationFile(services);

        services.TryAddSingleton<WriteApiTagArtifactsHandler>();
        services.TryAddSingleton<WriteApiTagArtifacts>(provider => provider.GetRequiredService<WriteApiTagArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteApiTagInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteApiTagInformationFileHandler>();
        services.TryAddSingleton<WriteApiTagInformationFile>(provider => provider.GetRequiredService<WriteApiTagInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiTagExtractor");
}