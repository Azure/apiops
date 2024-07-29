using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using System;
using System.Diagnostics;

namespace extractor;

internal static class AppModule
{
    public static void ConfigureRunApplication(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigureExtractNamedValues(builder);
        TagModule.ConfigureExtractTags(builder);
        GatewayModule.ConfigureExtractGateways(builder);
        VersionSetModule.ConfigureExtractVersionSets(builder);
        BackendModule.ConfigureExtractBackends(builder);
        LoggerModule.ConfigureExtractLoggers(builder);
        DiagnosticModule.ConfigureExtractDiagnostics(builder);
        PolicyFragmentModule.ConfigureExtractPolicyFragments(builder);
        ServicePolicyModule.ConfigureExtractServicePolicies(builder);
        ProductModule.ConfigureExtractProducts(builder);
        GroupModule.ConfigureExtractGroups(builder);
        SubscriptionModule.ConfigureExtractSubscriptions(builder);
        ApiModule.ConfigureExtractApis(builder);
        WorkspaceModule.ConfigureExtractWorkspaces(builder);
        builder.Services.AddFeatureManagement();

        builder.Services.TryAddSingleton(GetRunApplication);
    }

    private static RunApplication GetRunApplication(IServiceProvider provider)
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
        var extractWorkspaces = provider.GetRequiredService<ExtractWorkspaces>();
        var featureManager = provider.GetRequiredService<IFeatureManager>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var activity = activitySource.StartActivity(nameof(RunApplication));

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

            if (await featureManager.IsEnabledAsync("Workspaces"))
            {
                await extractWorkspaces(cancellationToken);
            }

            logger.LogInformation("Extractor completed.");
        };
    }
}