using Azure.Core.Pipeline;
using common;
using LanguageExt;
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

public delegate ValueTask ExtractProducts(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ProductName Name, ProductDto Dto)> ListProducts(CancellationToken cancellationToken);
public delegate bool ShouldExtractProduct(ProductName name);
public delegate ValueTask WriteProductArtifacts(ProductName name, ProductDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteProductInformationFile(ProductName name, ProductDto dto, CancellationToken cancellationToken);

internal static class ProductModule
{
    public static void ConfigureExtractProducts(IHostApplicationBuilder builder)
    {
        ConfigureListProducts(builder);
        ConfigureShouldExtractProduct(builder);
        ProductPolicyModule.ConfigureExtractProductPolicies(builder);
        ProductGroupModule.ConfigureExtractProductGroups(builder);
        ProductTagModule.ConfigureExtractProductTags(builder);
        ProductApiModule.ConfigureExtractProductApis(builder);
        ConfigureWriteProductArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractProducts);
    }

    private static ExtractProducts GetExtractProducts(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListProducts>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractProduct>();
        var writeArtifacts = provider.GetRequiredService<WriteProductArtifacts>();
        var extractProductPolicies = provider.GetRequiredService<ExtractProductPolicies>();
        var extractProductGroups = provider.GetRequiredService<ExtractProductGroups>();
        var extractProductTags = provider.GetRequiredService<ExtractProductTags>();
        var extractProductApis = provider.GetRequiredService<ExtractProductApis>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractProducts));

            logger.LogInformation("Extracting products...");

            await list(cancellationToken)
                    .Where(product => shouldExtract(product.Name))
                    .IterParallel(async product => await extractProduct(product.Name, product.Dto, cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractProduct(ProductName name, ProductDto dto, CancellationToken cancellationToken)
        {
            await writeArtifacts(name, dto, cancellationToken);
            await extractProductPolicies(name, cancellationToken);
            await extractProductGroups(name, cancellationToken);
            await extractProductTags(name, cancellationToken);
            await extractProductApis(name, cancellationToken);
        }
    }

    private static void ConfigureListProducts(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListProducts);
    }

    private static ListProducts GetListProducts(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            ProductsUri.From(serviceUri)
                       .List(pipeline, cancellationToken);
    }

    public static void ConfigureShouldExtractProduct(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractProduct);
    }

    private static ShouldExtractProduct GetShouldExtractProduct(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<ProductName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteProductArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteProductInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteProductArtifacts);
    }

    private static WriteProductArtifacts GetWriteProductArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteProductInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteProductInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteProductInformationFile);
    }

    private static WriteProductInformationFile GetWriteProductInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = ProductInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing product information file {ProductInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}