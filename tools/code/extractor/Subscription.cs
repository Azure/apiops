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

public delegate ValueTask ExtractSubscriptions(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(SubscriptionName Name, SubscriptionDto Dto)> ListSubscriptions(CancellationToken cancellationToken);
public delegate bool ShouldExtractSubscription(SubscriptionName name, SubscriptionDto dto);
public delegate ValueTask WriteSubscriptionArtifacts(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteSubscriptionInformationFile(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);

internal static class SubscriptionModule
{
    public static void ConfigureExtractSubscriptions(IHostApplicationBuilder builder)
    {
        ConfigureListSubscriptions(builder);
        ConfigureShouldExtractSubscription(builder);
        ConfigureWriteSubscriptionArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractSubscriptions);
    }

    private static ExtractSubscriptions GetExtractSubscriptions(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListSubscriptions>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractSubscription>();
        var writeArtifacts = provider.GetRequiredService<WriteSubscriptionArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractSubscriptions));

            logger.LogInformation("Extracting subscriptions...");

            await list(cancellationToken)
                    .Where(subscription => shouldExtract(subscription.Name, subscription.Dto))
                    .IterParallel(async subscription => await writeArtifacts(subscription.Name, subscription.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListSubscriptions(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListSubscriptions);
    }

    private static ListSubscriptions GetListSubscriptions(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            SubscriptionsUri.From(serviceUri)
                            .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractSubscription(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);
        ApiModule.ConfigureShouldExtractApiName(builder);
        ProductModule.ConfigureShouldExtractProduct(builder);

        builder.Services.TryAddSingleton(GetShouldExtractSubscription);
    }

    private static ShouldExtractSubscription GetShouldExtractSubscription(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();
        var shouldExtractApi = provider.GetRequiredService<ShouldExtractApiName>();
        var shouldExtractProduct = provider.GetRequiredService<ShouldExtractProduct>();

        var shouldExtractSubscriptionName = shouldExtractFactory.Create<SubscriptionName>();

        return (name, dto) =>
            // Don't extract the master subscription
            name != SubscriptionName.From("master")
            // Check name from configuration override
            && shouldExtractSubscriptionName(name)
            // Don't extract subscription if its API should not be extracted
            && common.SubscriptionModule.TryGetApiName(dto)
                                        .Map(shouldExtractApi.Invoke)
                                        .IfNone(true)
            // Don't extract subscription if its product should not be extracted
            && common.SubscriptionModule.TryGetProductName(dto)
                                        .Map(shouldExtractProduct.Invoke)
                                        .IfNone(true);
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