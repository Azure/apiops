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

public delegate ValueTask ExtractProductPolicies(ProductName productName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ProductPolicyName Name, ProductPolicyDto Dto)> ListProductPolicies(ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductPolicyArtifacts(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken);
public delegate ValueTask WriteProductPolicyFile(ProductPolicyName name, ProductPolicyDto dto, ProductName productName, CancellationToken cancellationToken);

internal static class ProductPolicyModule
{
    public static void ConfigureExtractProductPolicies(IHostApplicationBuilder builder)
    {
        ConfigureListProductPolicies(builder);
        ConfigureWriteProductPolicyArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractProductPolicies);
    }

    private static ExtractProductPolicies GetExtractProductPolicies(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListProductPolicies>();
        var writeArtifacts = provider.GetRequiredService<WriteProductPolicyArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (productName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractProductPolicies));

            logger.LogInformation("Extracting policies for product {ProductName}...", productName);

            await list(productName, cancellationToken)
                    .IterParallel(async policy => await writeArtifacts(policy.Name, policy.Dto, productName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListProductPolicies(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListProductPolicies);
    }

    private static ListProductPolicies GetListProductPolicies(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (productName, cancellationToken) =>
            ProductPoliciesUri.From(productName, serviceUri)
                              .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteProductPolicyArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteProductPolicyFile(builder);

        builder.Services.TryAddSingleton(GetWriteProductPolicyArtifacts);
    }

    private static WriteProductPolicyArtifacts GetWriteProductPolicyArtifacts(IServiceProvider provider)
    {
        var writePolicyFile = provider.GetRequiredService<WriteProductPolicyFile>();

        return async (name, dto, productName, cancellationToken) =>
            await writePolicyFile(name, dto, productName, cancellationToken);
    }

    private static void ConfigureWriteProductPolicyFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteProductPolicyFile);
    }

    private static WriteProductPolicyFile GetWriteProductPolicyFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, productName, cancellationToken) =>
        {
            var policyFile = ProductPolicyFile.From(name, productName, serviceDirectory);

            logger.LogInformation("Writing product policy file {PolicyFile}", policyFile);
            var policy = dto.Properties.Value ?? string.Empty;
            await policyFile.WritePolicy(policy, cancellationToken);
        };
    }
}