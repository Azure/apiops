using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
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
        ApiDiagnosticModule.ConfigurePutApiDiagnostics(builder);
        GatewayApiModule.ConfigurePutGatewayApis(builder);
        ProductPolicyModule.ConfigurePutProductPolicies(builder);
        ProductGroupModule.ConfigurePutProductGroups(builder);
        ProductTagModule.ConfigurePutProductTags(builder);
        ProductApiModule.ConfigurePutProductApis(builder);
        ApiOperationPolicyModule.ConfigurePutApiOperationPolicies(builder);
        WorkspaceNamedValueModule.ConfigurePutWorkspaceNamedValues(builder);
        WorkspaceBackendModule.ConfigurePutWorkspaceBackends(builder);
        WorkspaceTagModule.ConfigurePutWorkspaceTags(builder);
        WorkspaceVersionSetModule.ConfigurePutWorkspaceVersionSets(builder);
        WorkspaceLoggerModule.ConfigurePutWorkspaceLoggers(builder);
        WorkspaceDiagnosticModule.ConfigurePutWorkspaceDiagnostics(builder);
        WorkspacePolicyFragmentModule.ConfigurePutWorkspacePolicyFragments(builder);
        WorkspacePolicyModule.ConfigurePutWorkspacePolicies(builder);
        WorkspaceProductModule.ConfigurePutWorkspaceProducts(builder);
        WorkspaceGroupModule.ConfigurePutWorkspaceGroups(builder);
        WorkspaceApiModule.ConfigurePutWorkspaceApis(builder);
        WorkspaceApiModule.ConfigureDeleteWorkspaceApis(builder);
        WorkspaceGroupModule.ConfigureDeleteWorkspaceGroups(builder);
        WorkspaceProductModule.ConfigureDeleteWorkspaceProducts(builder);
        WorkspacePolicyModule.ConfigureDeleteWorkspacePolicies(builder);
        WorkspacePolicyFragmentModule.ConfigureDeleteWorkspacePolicyFragments(builder);
        WorkspaceDiagnosticModule.ConfigureDeleteWorkspaceDiagnostics(builder);
        WorkspaceLoggerModule.ConfigureDeleteWorkspaceLoggers(builder);
        WorkspaceVersionSetModule.ConfigureDeleteWorkspaceVersionSets(builder);
        WorkspaceTagModule.ConfigureDeleteWorkspaceTags(builder);
        WorkspaceBackendModule.ConfigureDeleteWorkspaceBackends(builder);
        WorkspaceNamedValueModule.ConfigureDeleteWorkspaceNamedValues(builder);
        ApiOperationPolicyModule.ConfigureDeleteApiOperationPolicies(builder);
        ProductApiModule.ConfigureDeleteProductApis(builder);
        ProductTagModule.ConfigureDeleteProductTags(builder);
        ProductGroupModule.ConfigureDeleteProductGroups(builder);
        ProductPolicyModule.ConfigureDeleteProductPolicies(builder);
        GatewayApiModule.ConfigureDeleteGatewayApis(builder);
        ApiDiagnosticModule.ConfigureDeleteApiDiagnostics(builder);
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
        builder.Services.AddFeatureManagement();

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
        var putApiDiagnostics = provider.GetRequiredService<PutApiDiagnostics>();
        var putGatewayApis = provider.GetRequiredService<PutGatewayApis>();
        var putProductPolicies = provider.GetRequiredService<PutProductPolicies>();
        var putProductGroups = provider.GetRequiredService<PutProductGroups>();
        var putProductTags = provider.GetRequiredService<PutProductTags>();
        var putProductApis = provider.GetRequiredService<PutProductApis>();
        var putApiOperationPolicies = provider.GetRequiredService<PutApiOperationPolicies>();
        var putWorkspaceNamedValues = provider.GetRequiredService<PutWorkspaceNamedValues>();
        var putWorkspaceBackends = provider.GetRequiredService<PutWorkspaceBackends>();
        var putWorkspaceTags = provider.GetRequiredService<PutWorkspaceTags>();
        var putWorkspaceVersionSets = provider.GetRequiredService<PutWorkspaceVersionSets>();
        var putWorkspaceLoggers = provider.GetRequiredService<PutWorkspaceLoggers>();
        var putWorkspaceDiagnostics = provider.GetRequiredService<PutWorkspaceDiagnostics>();
        var putWorkspacePolicyFragments = provider.GetRequiredService<PutWorkspacePolicyFragments>();
        var putWorkspacePolicies = provider.GetRequiredService<PutWorkspacePolicies>();
        var putWorkspaceProducts = provider.GetRequiredService<PutWorkspaceProducts>();
        var putWorkspaceGroups = provider.GetRequiredService<PutWorkspaceGroups>();
        var putWorkspaceApis = provider.GetRequiredService<PutWorkspaceApis>();
        var deleteWorkspaceApis = provider.GetRequiredService<DeleteWorkspaceApis>();
        var deleteWorkspaceGroups = provider.GetRequiredService<DeleteWorkspaceGroups>();
        var deleteWorkspaceProducts = provider.GetRequiredService<DeleteWorkspaceProducts>();
        var deleteWorkspacePolicies = provider.GetRequiredService<DeleteWorkspacePolicies>();
        var deleteWorkspacePolicyFragments = provider.GetRequiredService<DeleteWorkspacePolicyFragments>();
        var deleteWorkspaceDiagnostics = provider.GetRequiredService<DeleteWorkspaceDiagnostics>();
        var deleteWorkspaceLoggers = provider.GetRequiredService<DeleteWorkspaceLoggers>();
        var deleteWorkspaceVersionSets = provider.GetRequiredService<DeleteWorkspaceVersionSets>();
        var deleteWorkspaceTags = provider.GetRequiredService<DeleteWorkspaceTags>();
        var deleteWorkspaceBackends = provider.GetRequiredService<DeleteWorkspaceBackends>();
        var deleteWorkspaceNamedValues = provider.GetRequiredService<DeleteWorkspaceNamedValues>();
        var deleteApiOperationPolicies = provider.GetRequiredService<DeleteApiOperationPolicies>();
        var deleteProductApis = provider.GetRequiredService<DeleteProductApis>();
        var deleteProductTags = provider.GetRequiredService<DeleteProductTags>();
        var deleteProductGroups = provider.GetRequiredService<DeleteProductGroups>();
        var deleteProductPolicies = provider.GetRequiredService<DeleteProductPolicies>();
        var deleteGatewayApis = provider.GetRequiredService<DeleteGatewayApis>();
        var deleteApiDiagnostics = provider.GetRequiredService<DeleteApiDiagnostics>();
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
        var featureManager = provider.GetRequiredService<IFeatureManager>();
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
            await putApiDiagnostics(cancellationToken);
            await putGatewayApis(cancellationToken);
            await putProductPolicies(cancellationToken);
            await putProductGroups(cancellationToken);
            await putProductTags(cancellationToken);
            await putProductApis(cancellationToken);
            await putApiOperationPolicies(cancellationToken);

            if (await featureManager.IsEnabledAsync("Workspaces"))
            {
                await putWorkspaceNamedValues(cancellationToken);
                await putWorkspaceBackends(cancellationToken);
                await putWorkspaceTags(cancellationToken);
                await putWorkspaceVersionSets(cancellationToken);
                await putWorkspaceLoggers(cancellationToken);
                await putWorkspaceDiagnostics(cancellationToken);
                await putWorkspacePolicyFragments(cancellationToken);
                await putWorkspacePolicies(cancellationToken);
                await putWorkspaceProducts(cancellationToken);
                await putWorkspaceGroups(cancellationToken);
                await putWorkspaceApis(cancellationToken);
                await deleteWorkspaceApis(cancellationToken);
                await deleteWorkspaceGroups(cancellationToken);
                await deleteWorkspaceProducts(cancellationToken);
                await deleteWorkspacePolicies(cancellationToken);
                await deleteWorkspacePolicyFragments(cancellationToken);
                await deleteWorkspaceDiagnostics(cancellationToken);
                await deleteWorkspaceLoggers(cancellationToken);
                await deleteWorkspaceVersionSets(cancellationToken);
                await deleteWorkspaceTags(cancellationToken);
                await deleteWorkspaceBackends(cancellationToken);
                await deleteWorkspaceNamedValues(cancellationToken);
            }

            await deleteApiOperationPolicies(cancellationToken);
            await deleteProductApis(cancellationToken);
            await deleteProductTags(cancellationToken);
            await deleteProductGroups(cancellationToken);
            await deleteProductPolicies(cancellationToken);
            await deleteGatewayApis(cancellationToken);
            await deleteApiDiagnostics(cancellationToken);
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