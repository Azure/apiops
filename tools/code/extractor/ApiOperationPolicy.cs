using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractApiOperationPolicies(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiOperationPolicyName Name, ApiOperationPolicyDto Dto)> ListApiOperationPolicies(ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiOperationPolicyArtifacts(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiOperationPolicyFile(ApiOperationPolicyName name, ApiOperationPolicyDto dto, ApiOperationName apiOperationName, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiOperationPolicyModule
{
    public static void ConfigureExtractApiOperationPolicies(IHostApplicationBuilder builder)
    {
        ConfigureListApiOperationPolicies(builder);
        ConfigureWriteApiOperationPolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractApiOperationPolicies);
    }

    private static ExtractApiOperationPolicies GetExtractApiOperationPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiOperationPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteApiOperationPolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (operationName, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractApiOperationPolicies));

            logger.LogInformation("Extracting policies for operation {ApiOperationName} in API {ApiName}...", operationName, apiName);

            await list(operationName, apiName, cancellationToken)
                    .IterParallel(async policy => await writeArtifacts(policy.Name, policy.Dto, operationName, apiName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListApiOperationPolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApiOperationPolicies);
    }

    private static ListApiOperationPolicies GetListApiOperationPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (operationName, apiName, cancellationToken) =>
            ApiOperationPoliciesUri.From(operationName, apiName, serviceUri)
                                   .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteApiOperationPolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteApiOperationPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteApiOperationPolicyArtifacts);
    }

    private static WriteApiOperationPolicyArtifacts GetWriteApiOperationPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteApiOperationPolicyFile>();

        return async (name, dto, operationName, apiName, cancellationToken) =>
            await writePolicyFile(name, dto, operationName, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiOperationPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteApiOperationPolicyFile);
    }

    private static WriteApiOperationPolicyFile GetWriteApiOperationPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, operationName, apiName, cancellationToken) =>
        {
            var policyFile = ApiOperationPolicyFile.From(name, operationName, apiName, serviceDirectory);

            logger.LogInformation("Writing API operation policy file {ApiOperationPolicyFile}...", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}