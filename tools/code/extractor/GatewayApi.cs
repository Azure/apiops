using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractGatewayApis(GatewayName gatewayName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiName Name, GatewayApiDto Dto)> ListGatewayApis(GatewayName gatewayName, CancellationToken cancellationToken);
public delegate ValueTask WriteGatewayApiArtifacts(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken);
public delegate ValueTask WriteGatewayApiInformationFile(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken);

public static class GatewayApiModule
{
    public static void ConfigureExtractGatewayApis(IHostApplicationBuilder builder)
    {
        ConfigureListGatewayApis(builder);
        ApiModule.ConfigureShouldExtractApiName(builder);
        ConfigureWriteGatewayApiArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractGatewayApis);
    }

    private static ExtractGatewayApis GetExtractGatewayApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListGatewayApis>();
        var shouldExtractApi = provider.GetRequiredService<ShouldExtractApiName>();
        var writeArtifacts = provider.GetRequiredService<WriteGatewayApiArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (gatewayName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractGatewayApis));

            logger.LogInformation("Extracting APIs for gateway {GatewayName}...", gatewayName);

            await list(gatewayName, cancellationToken)
                    .Where(api => shouldExtractApi(api.Name))
                    .IterParallel(async gatewayapi => await writeArtifacts(gatewayapi.Name, gatewayapi.Dto, gatewayName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListGatewayApis(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListGatewayApis);
    }

    private static ListGatewayApis GetListGatewayApis(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (gatewyName, cancellationToken) =>
            GatewayApisUri.From(gatewyName, serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureWriteGatewayApiArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteGatewayApiInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteGatewayApiArtifacts);
    }

    private static WriteGatewayApiArtifacts GetWriteGatewayApiArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteGatewayApiInformationFile>();

        return async (name, dto, gatewayName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, gatewayName, cancellationToken);
        };
    }

    public static void ConfigureWriteGatewayApiInformationFile(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetWriteGatewayApiInformationFile);
    }

    private static WriteGatewayApiInformationFile GetWriteGatewayApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, gatewayName, cancellationToken) =>
        {
            var informationFile = GatewayApiInformationFile.From(name, gatewayName, serviceDirectory);

            logger.LogInformation("Writing gateway API information file {GatewayApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}