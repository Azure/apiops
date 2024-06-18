using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask RunExtractor(CancellationToken cancellationToken);

internal static class AppServices
{
    public static void ConfigureRunExtractor(IServiceCollection services)
    {
        NamedValueServices.ConfigureExtractNamedValues(services);
        TagServices.ConfigureExtractTags(services);
        GatewayServices.ConfigureExtractGateways(services);
        VersionSetServices.ConfigureExtractVersionSets(services);
        BackendServices.ConfigureExtractBackends(services);
        LoggerServices.ConfigureExtractLoggers(services);
        DiagnosticServices.ConfigureExtractDiagnostics(services);
        PolicyFragmentServices.ConfigureExtractPolicyFragments(services);
        ServicePolicyServices.ConfigureExtractServicePolicies(services);
        ProductServices.ConfigureExtractProducts(services);
        GroupServices.ConfigureExtractGroups(services);
        SubscriptionServices.ConfigureExtractSubscriptions(services);
        ApiServices.ConfigureExtractApis(services);

        services.TryAddSingleton(RunExtractor);
    }

    private static RunExtractor RunExtractor(IServiceProvider provider)
    {
        var extractNamedValues = provider.GetRequiredService<ExtractNamedValues>();
        var extractTags = provider.GetRequiredService<ExtractTags>();
        var extractGateways = provider.GetRequiredService<ExtractGateways>();
        var extractVersionSets = provider.GetRequiredService<ExtractVersionSets>();
        var extractBackends = provider.GetRequiredService<ExtractBackends>();
        var extractLoggers = provider.GetRequiredService<ExtractLoggers>();
        var extractDiagnostics = provider.GetRequiredService<ExtractDiagnostics>();
        var extractPolicyFragments = provider.GetRequiredService<ExtractPolicyFragments>();
        var extractServicePolicies = provider.GetRequiredService<ExtractServicePolicies>();
        var extractProducts = provider.GetRequiredService<ExtractProducts>();
        var extractGroups = provider.GetRequiredService<ExtractGroups>();
        var extractSubscriptions = provider.GetRequiredService<ExtractSubscriptions>();
        var extractApis = provider.GetRequiredService<ExtractApis>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = loggerFactory.CreateLogger("Extractor");

        return async cancellationToken =>
        {
            logger.LogInformation("Running extractor...");

            await extractNamedValues(cancellationToken);
            await extractTags(cancellationToken);
            await extractGateways(cancellationToken);
            await extractVersionSets(cancellationToken);
            await extractBackends(cancellationToken);
            await extractLoggers(cancellationToken);
            await extractDiagnostics(cancellationToken);
            await extractPolicyFragments(cancellationToken);
            await extractServicePolicies(cancellationToken);
            await extractProducts(cancellationToken);
            await extractGroups(cancellationToken);
            await extractSubscriptions(cancellationToken);
            await extractApis(cancellationToken);

            logger.LogInformation("Extractor completed.");
        };
    }
}