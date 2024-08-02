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

public delegate ValueTask ExtractProductGroups(ProductName productName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(GroupName Name, ProductGroupDto Dto)> ListProductGroups(ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductGroupArtifacts(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductGroupInformationFile(GroupName name, ProductGroupDto dto, ProductName productName, CancellationToken cancellationToken);

internal static class ProductGroupModule
{
    public static void ConfigureExtractProductGroups(IHostApplicationBuilder builder)
    {
        ConfigureListProductGroups(builder);
        ConfigureWriteProductGroupArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractProductGroups);
    }

    private static ExtractProductGroups GetExtractProductGroups(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListProductGroups>();
        var writeArtifacts = provider.GetRequiredService<WriteProductGroupArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractProductGroups));

            logger.LogInformation("Extracting groups for product {ProductName}...", productName);

            await list(productName, cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, productName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListProductGroups(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListProductGroups);
    }

    private static ListProductGroups GetListProductGroups(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationGroups = findConfigurationNamesFactory.Create<GroupName>();

        return (productName, cancellationToken) =>
        {
            var productGroupsUri = ProductGroupsUri.From(productName, serviceUri);
            var resources = productGroupsUri.List(pipeline, cancellationToken);
            return resources.Where(resource => shouldExtractGroup(resource.Name));
        };

        bool shouldExtractGroup(GroupName name) =>
            findConfigurationGroups()
                .Map(names => names.Contains(name))
                .IfNone(true);
    }

    private static void ConfigureWriteProductGroupArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteProductGroupInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteProductGroupArtifacts);
    }

    private static WriteProductGroupArtifacts GetWriteProductGroupArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteProductGroupInformationFile>();

        return async (name, dto, productName, cancellationToken) =>
            await writeInformationFile(name, dto, productName, cancellationToken);
    }

    private static void ConfigureWriteProductGroupInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteProductGroupInformationFile);
    }

    private static WriteProductGroupInformationFile GetWriteProductGroupInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            var informationFile = ProductGroupInformationFile.From(name, productName, serviceDirectory);

            logger.LogInformation("Writing product group information file {ProductGroupInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}