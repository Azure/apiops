using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractProductTags(ProductName productName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(TagName Name, ProductTagDto Dto)> ListProductTags(ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductTagArtifacts(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductTagInformationFile(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken);

file sealed class ExtractProductTagsHandler(ListProductTags list, WriteProductTagArtifacts writeArtifacts)
{
    public async ValueTask Handle(ProductName productName, CancellationToken cancellationToken) =>
        await list(productName, cancellationToken)
                .IterParallel(async producttag => await writeArtifacts(producttag.Name, producttag.Dto, productName, cancellationToken),
                              cancellationToken);
}

file sealed class ListProductTagsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(TagName, ProductTagDto)> Handle(ProductName productName, CancellationToken cancellationToken) =>
        ProductTagsUri.From(productName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteProductTagArtifactsHandler(WriteProductTagInformationFile writeTagFile)
{
    public async ValueTask Handle(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await writeTagFile(name, dto, productName, cancellationToken);
    }
}

file sealed class WriteProductTagInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, ProductTagDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        var informationFile = ProductTagInformationFile.From(name, productName, serviceDirectory);

        logger.LogInformation("Writing product tag information file {ProductTagInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class ProductTagServices
{
    public static void ConfigureExtractProductTags(IServiceCollection services)
    {
        ConfigureListProductTags(services);
        ConfigureWriteProductTagArtifacts(services);

        services.TryAddSingleton<ExtractProductTagsHandler>();
        services.TryAddSingleton<ExtractProductTags>(provider => provider.GetRequiredService<ExtractProductTagsHandler>().Handle);
    }

    private static void ConfigureListProductTags(IServiceCollection services)
    {
        services.TryAddSingleton<ListProductTagsHandler>();
        services.TryAddSingleton<ListProductTags>(provider => provider.GetRequiredService<ListProductTagsHandler>().Handle);
    }

    private static void ConfigureWriteProductTagArtifacts(IServiceCollection services)
    {
        ConfigureWriteProductTagInformationFile(services);

        services.TryAddSingleton<WriteProductTagArtifactsHandler>();
        services.TryAddSingleton<WriteProductTagArtifacts>(provider => provider.GetRequiredService<WriteProductTagArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteProductTagInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteProductTagInformationFileHandler>();
        services.TryAddSingleton<WriteProductTagInformationFile>(provider => provider.GetRequiredService<WriteProductTagInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ProductTagExtractor");
}