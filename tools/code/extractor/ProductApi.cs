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

public delegate ValueTask ExtractProductApis(ProductName productName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiName Name, ProductApiDto Dto)> ListProductApis(ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductApiArtifacts(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductApiInformationFile(ApiName name, ProductApiDto dto, ProductName productName, CancellationToken cancellationToken);

internal static class ProductApiModule
{
    public static void ConfigureExtractProductApis(IHostApplicationBuilder builder)
    {
        ConfigureListProductApis(builder);
        ConfigureWriteProductApiArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractProductApis);
    }

    private static ExtractProductApis GetExtractProductApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListProductApis>();
        var writeArtifacts = provider.GetRequiredService<WriteProductApiArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractProductApis));

            logger.LogInformation("Extracting APIs for product {ProductName}...", productName);

            await list(productName, cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, productName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListProductApis(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListProductApis);
    }

    private static ListProductApis GetListProductApis(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationApis = findConfigurationNamesFactory.Create<ApiName>();

        return (productName, cancellationToken) =>
        {
            var productApisUri = ProductApisUri.From(productName, serviceUri);
            var resources = productApisUri.List(pipeline, cancellationToken);
            return resources.Where(resource => shouldExtractApi(resource.Name));
        };

        bool shouldExtractApi(ApiName name) =>
            findConfigurationApis()
                .Map(names => names.Contains(name))
                .IfNone(true);
    }

    private static void ConfigureWriteProductApiArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteProductApiInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteProductApiArtifacts);
    }

    private static WriteProductApiArtifacts GetWriteProductApiArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteProductApiInformationFile>();

        return async (name, dto, productName, cancellationToken) =>
            await writeInformationFile(name, dto, productName, cancellationToken);
    }

    private static void ConfigureWriteProductApiInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteProductApiInformationFile);
    }

    private static WriteProductApiInformationFile GetWriteProductApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            var informationFile = ProductApiInformationFile.From(name, productName, serviceDirectory);

            logger.LogInformation("Writing product API information file {ProductApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}