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

internal delegate ValueTask ExtractApiOperations(ApiName apiName, CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<ApiOperationName> ListApiOperations(ApiName apiName, CancellationToken cancellationToken);

internal static class ApiOperationServices
{
    public static void ConfigureExtractApiOperations(IServiceCollection services)
    {
        ConfigureListApiOperations(services);
        ApiOperationPolicyServices.ConfigureExtractApiOperationPolicies(services);

        services.TryAddSingleton(ExtractApiOperations);
    }

    private static ExtractApiOperations ExtractApiOperations(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiOperations>();
        var extractPolicies = provider.GetRequiredService<ExtractApiOperationPolicies>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (apiName, cancellationToken) =>
            await list(apiName, cancellationToken)
                    .IterParallel(async name => await extractPolicies(name, apiName, cancellationToken),
                                  cancellationToken);
    }

    private static void ConfigureListApiOperations(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListApiOperations);
    }

    private static ListApiOperations ListApiOperations(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (apiName, cancellationToken) =>
            ApiOperationsUri.From(apiName, serviceUri)
                            .ListNames(pipeline, cancellationToken);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiOperationExtractor");
}