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

internal delegate ValueTask ExtractApiOperationPolicies(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<(ApiOperationPolicyName Name, ApiOperationPolicyDto Dto)> ListApiOperationPolicies(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiOperationPolicyArtifacts(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiOperationPolicyFile(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiOperationPolicyServices
{
    public static void ConfigureExtractApiOperationPolicies(IServiceCollection services)
    {
        ConfigureListApiOperationPolicies(services);
        ConfigureWriteApiOperationPolicyArtifacts(services);

        services.TryAddSingleton(ExtractApiOperationPolicies);
    }

    private static ExtractApiOperationPolicies ExtractApiOperationPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiOperationPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteApiOperationPolicyArtifacts>();

        return async (operationName, apiName, cancellationToken) =>
            await list(operationName, apiName, cancellationToken)
                    .IterParallel(async policy => await writeArtifacts(policy.Name, policy.Dto, operationName, apiName, cancellationToken),
                                  cancellationToken);
    }

    private static void ConfigureListApiOperationPolicies(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListApiOperationPolicies);
    }

    private static ListApiOperationPolicies ListApiOperationPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (operationName, apiName, cancellationToken) =>
            ApiOperationPoliciesUri.From(operationName, apiName, serviceUri)
                                   .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteApiOperationPolicyArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiOperationPolicyFile(services);

        services.TryAddSingleton(WriteApiOperationPolicyArtifacts);
    }

    private static WriteApiOperationPolicyArtifacts WriteApiOperationPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteApiOperationPolicyFile>();

        return async (name, dto, operationName, apiName, cancellationToken) =>
            await writePolicyFile(name, dto, operationName, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiOperationPolicyFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteApiOperationPolicyFile);
    }

    private static WriteApiOperationPolicyFile WriteApiOperationPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, dto, operationName, apiName, cancellationToken) =>
        {
            var policyFile = ApiOperationPolicyFile.From(name, operationName, apiName, serviceDirectory);

            logger.LogInformation("Writing API operation policy file {ApiOperationPolicyFile}...", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiOperationPolicyExtractor");
}