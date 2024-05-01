using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask PutApiReleaseInApim(ApiReleaseName name, ApiReleaseDto dto, ApiName apiName, CancellationToken cancellationToken);

internal delegate ValueTask DeleteApiReleaseFromApim(ApiReleaseName name, ApiName apiName, CancellationToken cancellationToken);

file sealed class PutApiReleaseInApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.CreateLogger(loggerFactory);

    public async ValueTask Handle(ApiReleaseName name, ApiReleaseDto dto, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Putting API release {ApiReleaseName} in API {ApiName}...", name, apiName);

        var uri = ApiReleaseUri.From(name, apiName, serviceUri);
        await uri.PutDto(dto, pipeline, cancellationToken);
    }
}

file sealed class DeleteApiReleaseFromApimHandler(ILoggerFactory loggerFactory, ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    private readonly ILogger logger = Common.CreateLogger(loggerFactory);

    public async ValueTask Handle(ApiReleaseName name, ApiName apiName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting API release {ApiReleaseName} from API {ApiName}...", name, apiName);

        var uri = ApiReleaseUri.From(name, apiName, serviceUri);
        await uri.Delete(pipeline, cancellationToken);
    }
}

internal static class ApiReleaseServices
{
    public static void ConfigurePutApiReleaseInApim(IServiceCollection services)
    {
        services.TryAddSingleton<PutApiReleaseInApimHandler>();
        services.TryAddSingleton<PutApiReleaseInApim>(provider => provider.GetRequiredService<PutApiReleaseInApimHandler>().Handle);
    }

    public static void ConfigureDeleteApiReleaseFromApim(IServiceCollection services)
    {
        services.TryAddSingleton<DeleteApiReleaseFromApimHandler>();
        services.TryAddSingleton<DeleteApiReleaseFromApim>(provider => provider.GetRequiredService<DeleteApiReleaseFromApimHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger CreateLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("ApiReleasePublisher");
}