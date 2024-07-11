using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace publisher;

internal static class AppModule
{
    public static void ConfigureRunApplication(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigurePutNamedValues(builder);
        GatewayModule.ConfigurePutGateways(builder);
        BackendModule.ConfigurePutBackends(builder);
        TagModule.ConfigurePutTags(builder);
        VersionSetModule.ConfigurePutVersionSets(builder);
        LoggerModule.ConfigurePutLoggers(builder);
        DiagnosticModule.ConfigurePutDiagnostics(builder);
        PolicyFragmentModule.ConfigurePutPolicyFragments(builder);
        ServicePolicyModule.ConfigurePutServicePolicies(builder);
        ProductModule.ConfigurePutProducts(builder);
        GroupModule.ConfigurePutGroups(builder);
        ApiModule.ConfigurePutApis(builder);
        SubscriptionModule.ConfigurePutSubscriptions(builder);
        ApiPolicyModule.ConfigurePutApiPolicies(builder);
        ApiTagModule.ConfigurePutApiTags(builder);
        GatewayApiModule.ConfigurePutGatewayApis(builder);
        ProductPolicyModule.ConfigurePutProductPolicies(builder);
        ProductGroupModule.ConfigurePutProductGroups(builder);
        ProductTagModule.ConfigurePutProductTags(builder);
        ProductApiModule.ConfigurePutProductApis(builder);
        ApiOperationPolicyModule.ConfigurePutApiOperationPolicies(builder);
        ApiOperationPolicyModule.ConfigureDeleteApiOperationPolicies(builder);
        ProductApiModule.ConfigureDeleteProductApis(builder);
        ProductTagModule.ConfigureDeleteProductTags(builder);
        ProductGroupModule.ConfigureDeleteProductGroups(builder);
        ProductPolicyModule.ConfigureDeleteProductPolicies(builder);
        GatewayApiModule.ConfigureDeleteGatewayApis(builder);
        ApiTagModule.ConfigureDeleteApiTags(builder);
        ApiPolicyModule.ConfigureDeleteApiPolicies(builder);
        SubscriptionModule.ConfigureDeleteSubscriptions(builder);
        ApiModule.ConfigureDeleteApis(builder);
        GroupModule.ConfigureDeleteGroups(builder);
        ProductModule.ConfigureDeleteProducts(builder);
        ServicePolicyModule.ConfigureDeleteServicePolicies(builder);
        PolicyFragmentModule.ConfigureDeletePolicyFragments(builder);
        DiagnosticModule.ConfigureDeleteDiagnostics(builder);
        LoggerModule.ConfigureDeleteLoggers(builder);
        VersionSetModule.ConfigureDeleteVersionSets(builder);
        TagModule.ConfigureDeleteTags(builder);
        BackendModule.ConfigureDeleteBackends(builder);
        GatewayModule.ConfigureDeleteGateways(builder);
        NamedValueModule.ConfigureDeleteNamedValues(builder);

        builder.Services.TryAddSingleton(GetRunApplication);
    }

    private static RunApplication GetRunApplication(IServiceProvider provider)
    {
        var putNamedValues = provider.GetRequiredService<PutNamedValues>();
        var putGateways = provider.GetRequiredService<PutGateways>();
        var putBackends = provider.GetRequiredService<PutBackends>();
        var putTags = provider.GetRequiredService<PutTags>();
        var putVersionSets = provider.GetRequiredService<PutVersionSets>();
        var putLoggers = provider.GetRequiredService<PutLoggers>();
        var putDiagnostics = provider.GetRequiredService<PutDiagnostics>();
        var putPolicyFragments = provider.GetRequiredService<PutPolicyFragments>();
        var putServicePolicies = provider.GetRequiredService<PutServicePolicies>();
        var putProducts = provider.GetRequiredService<PutProducts>();
        var putGroups = provider.GetRequiredService<PutGroups>();
        var putApis = provider.GetRequiredService<PutApis>();
        var putSubscriptions = provider.GetRequiredService<PutSubscriptions>();
        var putApiPolicies = provider.GetRequiredService<PutApiPolicies>();
        var putApiTags = provider.GetRequiredService<PutApiTags>();
        var putGatewayApis = provider.GetRequiredService<PutGatewayApis>();
        var putProductPolicies = provider.GetRequiredService<PutProductPolicies>();
        var putProductGroups = provider.GetRequiredService<PutProductGroups>();
        var putProductTags = provider.GetRequiredService<PutProductTags>();
        var putProductApis = provider.GetRequiredService<PutProductApis>();
        var putApiOperationPolicies = provider.GetRequiredService<PutApiOperationPolicies>();
        var deleteApiOperationPolicies = provider.GetRequiredService<DeleteApiOperationPolicies>();
        var deleteProductApis = provider.GetRequiredService<DeleteProductApis>();
        var deleteProductTags = provider.GetRequiredService<DeleteProductTags>();
        var deleteProductGroups = provider.GetRequiredService<DeleteProductGroups>();
        var deleteProductPolicies = provider.GetRequiredService<DeleteProductPolicies>();
        var deleteGatewayApis = provider.GetRequiredService<DeleteGatewayApis>();
        var deleteApiTags = provider.GetRequiredService<DeleteApiTags>();
        var deleteApiPolicies = provider.GetRequiredService<DeleteApiPolicies>();
        var deleteSubscriptions = provider.GetRequiredService<DeleteSubscriptions>();
        var deleteApis = provider.GetRequiredService<DeleteApis>();
        var deleteGroups = provider.GetRequiredService<DeleteGroups>();
        var deleteProducts = provider.GetRequiredService<DeleteProducts>();
        var deleteServicePolicies = provider.GetRequiredService<DeleteServicePolicies>();
        var deletePolicyFragments = provider.GetRequiredService<DeletePolicyFragments>();
        var deleteDiagnostics = provider.GetRequiredService<DeleteDiagnostics>();
        var deleteLoggers = provider.GetRequiredService<DeleteLoggers>();
        var deleteVersionSets = provider.GetRequiredService<DeleteVersionSets>();
        var deleteTags = provider.GetRequiredService<DeleteTags>();
        var deleteBackends = provider.GetRequiredService<DeleteBackends>();
        var deleteGateways = provider.GetRequiredService<DeleteGateways>();
        var deleteNamedValues = provider.GetRequiredService<DeleteNamedValues>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var activity = activitySource.StartActivity(nameof(RunApplication));

            logger.LogInformation("Running publisher...");

            await putNamedValues(cancellationToken);
            await putGateways(cancellationToken);
            await putTags(cancellationToken);
            await putVersionSets(cancellationToken);
            await putBackends(cancellationToken);
            await putLoggers(cancellationToken);
            await putDiagnostics(cancellationToken);
            await putPolicyFragments(cancellationToken);
            await putServicePolicies(cancellationToken);
            await putProducts(cancellationToken);
            await putGroups(cancellationToken);
            await putApis(cancellationToken);
            await putSubscriptions(cancellationToken);
            await putApiPolicies(cancellationToken);
            await putApiTags(cancellationToken);
            await putGatewayApis(cancellationToken);
            await putProductPolicies(cancellationToken);
            await putProductGroups(cancellationToken);
            await putProductTags(cancellationToken);
            await putProductApis(cancellationToken);
            await putApiOperationPolicies(cancellationToken);
            await deleteApiOperationPolicies(cancellationToken);
            await deleteProductApis(cancellationToken);
            await deleteProductTags(cancellationToken);
            await deleteProductGroups(cancellationToken);
            await deleteProductPolicies(cancellationToken);
            await deleteGatewayApis(cancellationToken);
            await deleteApiTags(cancellationToken);
            await deleteApiPolicies(cancellationToken);
            await deleteSubscriptions(cancellationToken);
            await deleteApis(cancellationToken);
            await deleteGroups(cancellationToken);
            await deleteProducts(cancellationToken);
            await deleteServicePolicies(cancellationToken);
            await deletePolicyFragments(cancellationToken);
            await deleteDiagnostics(cancellationToken);
            await deleteLoggers(cancellationToken);
            await deleteVersionSets(cancellationToken);
            await deleteTags(cancellationToken);
            await deleteBackends(cancellationToken);
            await deleteGateways(cancellationToken);
            await deleteNamedValues(cancellationToken);

            logger.LogInformation("Publisher completed.");
        };
    }
}