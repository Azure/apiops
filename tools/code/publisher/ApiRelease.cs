using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutApiReleaseInApim(ApiReleaseName name, ApiReleaseDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiReleaseFromApim(ApiReleaseName name, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiReleaseModule
{
    public static void ConfigurePutApiReleaseInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiReleaseInApim);
    }

    private static PutApiReleaseInApim GetPutApiReleaseInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            logger.LogInformation("Putting API release {ApiReleaseName} in API {ApiName}...", name, apiName);

            await ApiReleaseUri.From(name, apiName, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteApiReleaseFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApiReleaseFromApim);
    }

    private static DeleteApiReleaseFromApim GetDeleteApiReleaseFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, apiName, cancellationToken) =>
        {
            logger.LogInformation("Deleting API release {ApiReleaseName} from API {ApiName}...", name, apiName);

            await ApiReleaseUri.From(name, apiName, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}