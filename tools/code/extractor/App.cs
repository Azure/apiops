using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask RunExtractor(CancellationToken cancellationToken);

file sealed class RunExtractorHandler(ILoggerFactory loggerFactory,
                                      ExtractNamedValues extractNamedValues,
                                      ExtractTags extractTags,
                                      ExtractGateways extractGateways,
                                      ExtractVersionSets extractVersionSets,
                                      ExtractBackends extractBackends,
                                      ExtractLoggers extractLoggers,
                                      ExtractDiagnostics extractDiagnostics,
                                      ExtractPolicyFragments extractPolicyFragments,
                                      ExtractServicePolicies extractServicePolicies,
                                      ExtractProducts extractProducts,
                                      ExtractGroups extractGroups,
                                      ExtractSubscriptions extractSubscriptions,
                                      ExtractApis extractApis)
{
    private readonly ILogger logger = loggerFactory.CreateLogger("RunExtractor");

    public async ValueTask Handle(CancellationToken cancellationToken)
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
    }
}

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

        services.TryAddSingleton<RunExtractorHandler>();
        services.TryAddSingleton<RunExtractor>(provider => provider.GetRequiredService<RunExtractorHandler>().Handle);
    }
}