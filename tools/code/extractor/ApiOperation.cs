using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApiOperations(ApiName apiName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<ApiOperationName> ListApiOperations(ApiName apiName, CancellationToken cancellationToken);

file sealed class ExtractApiOperationsHandler(ListApiOperations list, ExtractApiOperationPolicies extractApiOperationPolicies)
{
    public async ValueTask Handle(ApiName apiName, CancellationToken cancellationToken) =>
        await list(apiName, cancellationToken)
                .IterParallel(async name => await ExtractApiOperation(name, apiName, cancellationToken),
                              cancellationToken);

    private async ValueTask ExtractApiOperation(ApiOperationName name, ApiName apiName, CancellationToken cancellationToken)
    {
        await extractApiOperationPolicies(name, apiName, cancellationToken);
    }
}

file sealed class ListApiOperationsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<ApiOperationName> Handle(ApiName apiName, CancellationToken cancellationToken) =>
        ApiOperationsUri.From(apiName, serviceUri).ListNames(pipeline, cancellationToken);
}

internal static class ApiOperationServices
{
    public static void ConfigureExtractApiOperations(IServiceCollection services)
    {
        ConfigureListApiOperations(services);
        ApiOperationPolicyServices.ConfigureExtractApiOperationPolicies(services);

        services.TryAddSingleton<ExtractApiOperationsHandler>();
        services.TryAddSingleton<ExtractApiOperations>(provider => provider.GetRequiredService<ExtractApiOperationsHandler>().Handle);
    }

    private static void ConfigureListApiOperations(IServiceCollection services)
    {
        services.TryAddSingleton<ListApiOperationsHandler>();
        services.TryAddSingleton<ListApiOperations>(provider => provider.GetRequiredService<ListApiOperationsHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiOperationExtractor");
}