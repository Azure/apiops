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

internal delegate ValueTask ExtractSubscriptions(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(SubscriptionName Name, SubscriptionDto Dto)> ListSubscriptions(CancellationToken cancellationToken);

file delegate bool ShouldExtractSubscription(SubscriptionName name, SubscriptionDto dto);

file delegate ValueTask WriteSubscriptionArtifacts(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteSubscriptionInformationFile(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);

file sealed class ExtractSubscriptionsHandler(ListSubscriptions list, ShouldExtractSubscription shouldExtractSubscription, WriteSubscriptionArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(subscription => shouldExtractSubscription(subscription.Name, subscription.Dto))
                .IterParallel(async subscription => await writeArtifacts(subscription.Name, subscription.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListSubscriptionsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(SubscriptionName, SubscriptionDto)> Handle(CancellationToken cancellationToken) =>
        SubscriptionsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractSubscriptionHandler(ShouldExtractFactory shouldExtractFactory, ShouldExtractApiName shouldExtractApi, ShouldExtractProduct shouldExtractProduct)
{
    public bool Handle(SubscriptionName name, SubscriptionDto dto) =>
        // Don't extract the master subscription
        name != SubscriptionName.From("master")
        // Check name from configuration override
        && shouldExtractFactory.Create<SubscriptionName>().Invoke(name)
        // Don't extract subscription if its API should not be extracted
        && SubscriptionModule.TryGetApiName(dto)
                             .Map(shouldExtractApi.Invoke)
                             .IfNone(true)
        // Don't extract subscription if its product should not be extracted
        && SubscriptionModule.TryGetProductName(dto)
                             .Map(shouldExtractProduct.Invoke)
                             .IfNone(true);

}

file sealed class WriteSubscriptionArtifactsHandler(WriteSubscriptionInformationFile writeInformationFile)
{
    public async ValueTask Handle(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteSubscriptionInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken)
    {
        var informationFile = SubscriptionInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing subscription information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class SubscriptionServices
{
    public static void ConfigureExtractSubscriptions(IServiceCollection services)
    {
        ConfigureListSubscriptions(services);
        ConfigureShouldExtractSubscription(services);
        ConfigureWriteSubscriptionArtifacts(services);

        services.TryAddSingleton<ExtractSubscriptionsHandler>();
        services.TryAddSingleton<ExtractSubscriptions>(provider => provider.GetRequiredService<ExtractSubscriptionsHandler>().Handle);
    }

    private static void ConfigureListSubscriptions(IServiceCollection services)
    {
        services.TryAddSingleton<ListSubscriptionsHandler>();
        services.TryAddSingleton<ListSubscriptions>(provider => provider.GetRequiredService<ListSubscriptionsHandler>().Handle);
    }

    private static void ConfigureShouldExtractSubscription(IServiceCollection services)
    {
        ApiServices.ConfigureShouldExtractApiName(services);
        ProductServices.ConfigureShouldExtractProduct(services);

        services.TryAddSingleton<ShouldExtractSubscriptionHandler>();
        services.TryAddSingleton<ShouldExtractSubscription>(provider => provider.GetRequiredService<ShouldExtractSubscriptionHandler>().Handle);
    }

    private static void ConfigureWriteSubscriptionArtifacts(IServiceCollection services)
    {
        ConfigureWriteSubscriptionInformationFile(services);

        services.TryAddSingleton<WriteSubscriptionArtifactsHandler>();
        services.TryAddSingleton<WriteSubscriptionArtifacts>(provider => provider.GetRequiredService<WriteSubscriptionArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteSubscriptionInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteSubscriptionInformationFileHandler>();
        services.TryAddSingleton<WriteSubscriptionInformationFile>(provider => provider.GetRequiredService<WriteSubscriptionInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("SubscriptionExtractor");
}