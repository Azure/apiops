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

public delegate ValueTask ExtractServicePolicies(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ServicePolicyName Name, ServicePolicyDto Dto)> ListServicePolicies(CancellationToken cancellationToken);
public delegate ValueTask WriteServicePolicyArtifacts(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteServicePolicyFile(ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken);

internal static class ServicePolicyModule
{
    public static void ConfigureExtractServicePolicies(IHostApplicationBuilder builder)
    {
        ConfigureListServicePolicies(builder);
        ConfigureWriteServicePolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractServicePolicies);
    }

    private static ExtractServicePolicies GetExtractServicePolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListServicePolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteServicePolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractServicePolicies));

            logger.LogInformation("Extracting service policies...");

            await list(cancellationToken)
                    .IterParallel(async servicepolicy => await writeArtifacts(servicepolicy.Name, servicepolicy.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListServicePolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListServicePolicies);
    }

    private static ListServicePolicies GetListServicePolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            ServicePoliciesUri.From(serviceUri)
                              .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteServicePolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteServicePolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteServicePolicyArtifacts);
    }

    private static WriteServicePolicyArtifacts GetWriteServicePolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteServicePolicyFile>();

        return async (ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken) =>
        {
            await writePolicyFile(name, dto, cancellationToken);
        };
    }

    private static void ConfigureWriteServicePolicyFile(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteServicePolicyFile);
    }

    private static WriteServicePolicyFile GetWriteServicePolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (ServicePolicyName name, ServicePolicyDto dto, CancellationToken cancellationToken) =>
        {
            var policyFile = ServicePolicyFile.From(name, serviceDirectory);

            logger.LogInformation("Writing service policy file {ServicePolicyFile}...", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}