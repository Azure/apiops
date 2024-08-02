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

public delegate ValueTask ExtractSubscriptions(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(SubscriptionName Name, SubscriptionDto Dto)> ListSubscriptions(CancellationToken cancellationToken);
public delegate ValueTask WriteSubscriptionArtifacts(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteSubscriptionInformationFile(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);

internal static class SubscriptionModule
{
    public static void ConfigureExtractSubscriptions(IHostApplicationBuilder builder)
    {
        ConfigureListSubscriptions(builder);
        ConfigureWriteSubscriptionArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractSubscriptions);
    }

    private static ExtractSubscriptions GetExtractSubscriptions(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListSubscriptions>();
        var writeArtifacts = provider.GetRequiredService<WriteSubscriptionArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractSubscriptions));

            logger.LogInformation("Extracting subscriptions...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListSubscriptions(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListSubscriptions);
    }

    private static ListSubscriptions GetListSubscriptions(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationSubscriptions = findConfigurationNamesFactory.Create<SubscriptionName>();
        var findConfigurationApis = findConfigurationNamesFactory.Create<ApiName>();
        var findConfigurationProducts = findConfigurationNamesFactory.Create<ProductName>();

        return cancellationToken =>
            findConfigurationSubscriptions()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken))
                .Where(resource => shouldExtractSubscription(resource.Name, resource.Dto));

        IAsyncEnumerable<(SubscriptionName Name, SubscriptionDto Dto)> listFromSet(IEnumerable<SubscriptionName> names, CancellationToken cancellationToken) =>
            names.Select(name => SubscriptionUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(SubscriptionName, SubscriptionDto)> listAll(CancellationToken cancellationToken)
        {
            var subscriptionsUri = SubscriptionsUri.From(serviceUri);
            return subscriptionsUri.List(pipeline, cancellationToken);
        }

        bool shouldExtractSubscription(SubscriptionName name, SubscriptionDto dto)
        {
            var apiNamesOption = findConfigurationApis();
            var productNamesOption = findConfigurationProducts();

            var shouldExtractApi = (ApiName apiName) =>
                apiNamesOption.Map(names => names.Contains(apiName))
                              .IfNone(true);

            var shouldExtractProduct = (ProductName productName) =>
                productNamesOption.Map(names => names.Contains(productName))
                                  .IfNone(true);

            // Don't extract the master subscription
            return name != SubscriptionName.From("master")
                    // Don't extract subscription if its API should not be extracted
                    && common.SubscriptionModule.TryGetApiName(dto)
                                                .Map(shouldExtractApi)
                                                .IfNone(true)
                    // Don't extract subscription if its product should not be extracted
                    && common.SubscriptionModule.TryGetProductName(dto)
                                                .Map(shouldExtractProduct)
                                                .IfNone(true);
        }
    }

    private static void ConfigureWriteSubscriptionArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteSubscriptionInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteSubscriptionArtifacts);
    }

    private static WriteSubscriptionArtifacts GetWriteSubscriptionArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteSubscriptionInformationFile>();

        return async (name, dto, cancellationToken) =>
        {
            await writeInformationFile(name, dto, cancellationToken);
        };
    }

    private static void ConfigureWriteSubscriptionInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteSubscriptionInformationFile);
    }

    private static WriteSubscriptionInformationFile GetWriteSubscriptionInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = SubscriptionInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing subscription information file {SubscriptionInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}