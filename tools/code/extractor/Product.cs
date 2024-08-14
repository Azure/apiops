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

public delegate ValueTask ExtractProducts(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ProductName Name, ProductDto Dto)> ListProducts(CancellationToken cancellationToken);
public delegate ValueTask WriteProductArtifacts(ProductName name, ProductDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteProductInformationFile(ProductName name, ProductDto dto, CancellationToken cancellationToken);

internal static class ProductModule
{
    public static void ConfigureExtractProducts(IHostApplicationBuilder builder)
    {
        ConfigureListProducts(builder);
        ConfigureWriteProductArtifacts(builder);
        ProductPolicyModule.ConfigureExtractProductPolicies(builder);
        ProductGroupModule.ConfigureExtractProductGroups(builder);
        ProductTagModule.ConfigureExtractProductTags(builder);
        ProductApiModule.ConfigureExtractProductApis(builder);

        builder.Services.TryAddSingleton(GetExtractProducts);
    }

    private static ExtractProducts GetExtractProducts(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListProducts>();
        var writeArtifacts = provider.GetRequiredService<WriteProductArtifacts>();
        var extractPolicies = provider.GetRequiredService<ExtractProductPolicies>();
        var extractGroups = provider.GetRequiredService<ExtractProductGroups>();
        var extractTags = provider.GetRequiredService<ExtractProductTags>();
        var extractApis = provider.GetRequiredService<ExtractProductApis>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractProducts));

            logger.LogInformation("Extracting products...");

            await list(cancellationToken)
                    .IterParallel(async resource => await extractProduct(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractProduct(ProductName name, ProductDto dto, CancellationToken cancellationToken)
        {
            await writeArtifacts(name, dto, cancellationToken);
            await extractPolicies(name, cancellationToken);
            await extractGroups(name, cancellationToken);
            await extractTags(name, cancellationToken);
            await extractApis(name, cancellationToken);
        }
    }

    private static void ConfigureListProducts(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListProducts);
    }

    private static ListProducts GetListProducts(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<ProductName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(ProductName, ProductDto)> listFromSet(IEnumerable<ProductName> names, CancellationToken cancellationToken) =>
            names.Select(name => ProductUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(ProductName, ProductDto)> listAll(CancellationToken cancellationToken)
        {
            var productsUri = ProductsUri.From(serviceUri);
            return productsUri.List(pipeline, cancellationToken);
        }
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