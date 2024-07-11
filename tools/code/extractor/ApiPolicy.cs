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

public delegate ValueTask ExtractApiPolicies(ApiName apiName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiPolicyName Name, ApiPolicyDto Dto)> ListApiPolicies(ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiPolicyArtifacts(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiPolicyFile(ApiPolicyName name, ApiPolicyDto dto, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiPolicyModule
{
    public static void ConfigureExtractApiPolicies(IHostApplicationBuilder builder)
    {
        ConfigureListApiPolicies(builder);
        ConfigureWriteApiPolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractApiPolicies);
    }

    private static ExtractApiPolicies GetExtractApiPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteApiPolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractApiPolicies));

            logger.LogInformation("Extracting policies for API {ApiName}...", apiName);

            await list(apiName, cancellationToken)
                    .IterParallel(async policy => await writeArtifacts(policy.Name, policy.Dto, apiName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListApiPolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApiPolicies);
    }

    private static ListApiPolicies GetListApiPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (apiName, cancellationToken) =>
            ApiPoliciesUri.From(apiName, serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteApiPolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteApiPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteApiPolicyArtifacts);
    }

    private static WriteApiPolicyArtifacts GetWriteApiPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteApiPolicyFile>();

        return async (name, dto, apiName, cancellationToken) =>
            await writePolicyFile(name, dto, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteApiPolicyFile);
    }

    private static WriteApiPolicyFile GetWriteApiPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            var policyFile = ApiPolicyFile.From(name, apiName, serviceDirectory);

            logger.LogInformation("Writing API policy file {PolicyFile}", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}