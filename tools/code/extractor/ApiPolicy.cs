using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApiPolicies(ApiName apiName, CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<(ApiPolicyName Name, ApiPolicyDto Dto)> ListApiPolicies(ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiPolicyArtifacts(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiPolicyFile(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiPolicyServices
{
    public static void ConfigureExtractApiPolicies(IServiceCollection services)
    {
        ConfigureListApiPolicies(services);
        ConfigureWriteApiPolicyArtifacts(services);

        services.TryAddSingleton(ExtractApiPolicies);
    }

    private static ExtractApiPolicies ExtractApiPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteApiPolicyArtifacts>();

        return async (apiName, cancellationToken) =>
            await list(apiName, cancellationToken)
                    .IterParallel(async policy => await writeArtifacts(policy.Name, policy.Dto, apiName, cancellationToken),
                                  cancellationToken);
    }

    private static void ConfigureListApiPolicies(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListApiPolicies);
    }

    private static ListApiPolicies ListApiPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (apiName, cancellationToken) =>
            ApiPoliciesUri.From(apiName, serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteApiPolicyArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiPolicyFile(services);

        services.TryAddSingleton(WriteApiPolicyArtifacts);
    }

    private static WriteApiPolicyArtifacts WriteApiPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteApiPolicyFile>();

        return async (name, dto, apiName, cancellationToken) =>
            await writePolicyFile(name, dto, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiPolicyFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteApiPolicyFile);
    }

    private static WriteApiPolicyFile WriteApiPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, dto, apiName, cancellationToken) =>
        {
            var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

            logger.LogInformation("Writing API policy file {PolicyFile}", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiPolicyExtractor");
}