using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractProducts(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ProductName Name, ProductDto Dto)> ListProducts(CancellationToken cancellationToken);

internal delegate bool ShouldExtractProduct(ProductName name);

file delegate ValueTask WriteProductArtifacts(ProductName name, ProductDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteProductInformationFile(ProductName name, ProductDto dto, CancellationToken cancellationToken);

file sealed class ExtractProductsHandler(ListProducts list,
                                         ShouldExtractProduct shouldExtract,
                                         WriteProductArtifacts writeArtifacts,
                                         ExtractProductPolicies extractProductPolicies,
                                         ExtractProductGroups extractProductGroups,
                                         ExtractProductTags extractProductTags,
                                         ExtractProductApis extractProductApis)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(product => shouldExtract(product.Name))
                .IterParallel(async product => await ExtractProduct(product.Name, product.Dto, cancellationToken),
                              cancellationToken);

    private async ValueTask ExtractProduct(ProductName name, ProductDto dto, CancellationToken cancellationToken)
    {
        await writeArtifacts(name, dto, cancellationToken);
        await extractProductPolicies(name, cancellationToken);
        await extractProductGroups(name, cancellationToken);
        await extractProductTags(name, cancellationToken);
        await extractProductApis(name, cancellationToken);
    }
}

file sealed class ListProductsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ProductName, ProductDto)> Handle(CancellationToken cancellationToken) =>
        ProductsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractProductHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(ProductName name)
    {
        var shouldExtract = shouldExtractFactory.Create<ProductName>();
        return shouldExtract(name);
    }
}

file sealed class WriteProductArtifactsHandler(WriteProductInformationFile writeInformationFile)
{
    public async ValueTask Handle(ProductName name, ProductDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteProductInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ProductName name, ProductDto dto, CancellationToken cancellationToken)
    {
        var informationFile = ProductInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing product information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class ProductServices
{
    public static void ConfigureExtractProducts(IServiceCollection services)
    {
        ConfigureListProducts(services);
        ConfigureShouldExtractProduct(services);
        ConfigureWriteProductArtifacts(services);
        ProductPolicyServices.ConfigureExtractProductPolicies(services);
        ProductGroupServices.ConfigureExtractProductGroups(services);
        ProductTagServices.ConfigureExtractProductTags(services);
        ProductApiServices.ConfigureExtractProductApis(services);

        services.TryAddSingleton<ExtractProductsHandler>();
        services.TryAddSingleton<ExtractProducts>(provider => provider.GetRequiredService<ExtractProductsHandler>().Handle);
    }

    private static void ConfigureListProducts(IServiceCollection services)
    {
        services.TryAddSingleton<ListProductsHandler>();
        services.TryAddSingleton<ListProducts>(provider => provider.GetRequiredService<ListProductsHandler>().Handle);
    }

    public static void ConfigureShouldExtractProduct(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractProductHandler>();
        services.TryAddSingleton<ShouldExtractProduct>(provider => provider.GetRequiredService<ShouldExtractProductHandler>().Handle);
    }

    private static void ConfigureWriteProductArtifacts(IServiceCollection services)
    {
        ConfigureWriteProductInformationFile(services);

        services.TryAddSingleton<WriteProductArtifactsHandler>();
        services.TryAddSingleton<WriteProductArtifacts>(provider => provider.GetRequiredService<WriteProductArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteProductInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteProductInformationFileHandler>();
        services.TryAddSingleton<WriteProductInformationFile>(provider => provider.GetRequiredService<WriteProductInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ProductExtractor");
}