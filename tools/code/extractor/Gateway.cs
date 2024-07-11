using Azure.Core.Pipeline;
using common;
using LanguageExt;
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

public delegate ValueTask ExtractGateways(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(GatewayName Name, GatewayDto Dto)> ListGateways(CancellationToken cancellationToken);
public delegate bool ShouldExtractGateway(GatewayName name);
public delegate ValueTask WriteGatewayArtifacts(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteGatewayInformationFile(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);

internal static class GatewayModule
{
    public static void ConfigureExtractGateways(IHostApplicationBuilder builder)
    {
        ConfigureListGateways(builder);
        ConfigureShouldExtractGateway(builder);
        ConfigureWriteGatewayArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractGateways);
    }

    private static ExtractGateways GetExtractGateways(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListGateways>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractGateway>();
        var writeArtifacts = provider.GetRequiredService<WriteGatewayArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractGateways));

            logger.LogInformation("Extracting gateways...");

            await list(cancellationToken)
                    .Where(gateway => shouldExtract(gateway.Name))
                    .IterParallel(async gateway => await writeArtifacts(gateway.Name, gateway.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListGateways(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListGateways);
    }

    private static ListGateways GetListGateways(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            GatewaysUri.From(serviceUri)
                       .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractGateway(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractGateway);
    }

    private static ShouldExtractGateway GetShouldExtractGateway(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<GatewayName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteGatewayArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteGatewayInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteGatewayArtifacts);
    }

    private static WriteGatewayArtifacts GetWriteGatewayArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteGatewayInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteGatewayInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteGatewayInformationFile);
    }

    private static WriteGatewayInformationFile GetWriteGatewayInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = GatewayInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing gateway information file {GatewayInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}