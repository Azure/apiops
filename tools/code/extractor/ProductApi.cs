using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractProductApis(ProductName productName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ApiName Name, ProductApiDto Dto)> ListProductApis(ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductApiArtifacts(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken);

file delegate ValueTask WriteProductApiInformationFile(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken);

file sealed class ExtractProductApisHandler(ListProductApis list, ShouldExtractApiName shouldExtractApi, WriteProductApiArtifacts writeArtifacts)
{
    public async ValueTask Handle(ProductName productName, CancellationToken cancellationToken) =>
        await list(productName, cancellationToken)
                .Where(api => shouldExtractApi(api.Name))
                .IterParallel(async productapi => await writeArtifacts(productapi.Name, productapi.Dto, productName, cancellationToken),
                              cancellationToken);
}

file sealed class ListProductApisHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ApiName, ProductApiDto)> Handle(ProductName productName, CancellationToken cancellationToken) =>
        ProductApisUri.From(productName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteProductApiArtifactsHandler(WriteProductApiInformationFile writeApiFile)
{
    public async ValueTask Handle(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        await writeApiFile(name, dto, productName, cancellationToken);
    }
}

file sealed class WriteProductApiInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken)
    {
        var informationFile = ProductApiInformationFile.From(name, productName, serviceDirectory);

        logger.LogInformation("Writing product api information file {ProductApiInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class ProductApiServices
{
    public static void ConfigureExtractProductApis(IServiceCollection services)
    {
        ConfigureListProductApis(services);
        ConfigureWriteProductApiArtifacts(services);
        ApiServices.ConfigureShouldExtractApiName(services);

        services.TryAddSingleton<ExtractProductApisHandler>();
        services.TryAddSingleton<ExtractProductApis>(provider => provider.GetRequiredService<ExtractProductApisHandler>().Handle);
    }

    private static void ConfigureListProductApis(IServiceCollection services)
    {
        services.TryAddSingleton<ListProductApisHandler>();
        services.TryAddSingleton<ListProductApis>(provider => provider.GetRequiredService<ListProductApisHandler>().Handle);
    }

    private static void ConfigureWriteProductApiArtifacts(IServiceCollection services)
    {
        ConfigureWriteProductApiInformationFile(services);

        services.TryAddSingleton<WriteProductApiArtifactsHandler>();
        services.TryAddSingleton<WriteProductApiArtifacts>(provider => provider.GetRequiredService<WriteProductApiArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteProductApiInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteProductApiInformationFileHandler>();
        services.TryAddSingleton<WriteProductApiInformationFile>(provider => provider.GetRequiredService<WriteProductApiInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ProductApiExtractor");
}