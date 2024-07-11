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

public delegate ValueTask ExtractApiOperations(ApiName apiName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<ApiOperationName> ListApiOperations(ApiName apiName, CancellationToken cancellationToken);

internal static class ApiOperationModule
{
    public static void ConfigureExtractApiOperations(IHostApplicationBuilder builder)
    {
        ConfigureListApiOperations(builder);
        ApiOperationPolicyModule.ConfigureExtractApiOperationPolicies(builder);

        builder.Services.TryAddSingleton(GetExtractApiOperations);
    }

    private static ExtractApiOperations GetExtractApiOperations(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiOperations>();
        var extractPolicies = provider.GetRequiredService<ExtractApiOperationPolicies>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractApiOperations));

            logger.LogInformation("Extracting API operations for {ApiName}...", apiName);

            await list(apiName, cancellationToken)
                    .IterParallel(async name => await extractPolicies(name, apiName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListApiOperations(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApiOperations);
    }

    private static ListApiOperations GetListApiOperations(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (apiName, cancellationToken) =>
            ApiOperationsUri.From(apiName, serviceUri)
                            .ListNames(pipeline, cancellationToken);
    }
}