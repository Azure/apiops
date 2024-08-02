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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractGateways(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(GatewayName Name, GatewayDto Dto)> ListGateways(CancellationToken cancellationToken);
public delegate ValueTask WriteGatewayArtifacts(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteGatewayInformationFile(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);

internal static class GatewayModule
{
    public static void ConfigureExtractGateways(IHostApplicationBuilder builder)
    {
        ConfigureListGateways(builder);
        ConfigureWriteGatewayArtifacts(builder);
        GatewayApiModule.ConfigureExtractGatewayApis(builder);

        builder.Services.TryAddSingleton(GetExtractGateways);
    }

    private static ExtractGateways GetExtractGateways(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListGateways>();
        var writeArtifacts = provider.GetRequiredService<WriteGatewayArtifacts>();
        var extractGatewayApis = provider.GetRequiredService<ExtractGatewayApis>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractGateways));

            logger.LogInformation("Extracting gateways...");

            await list(cancellationToken)
                    .IterParallel(async resource => await extractGateway(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractGateway(GatewayName name, GatewayDto dto, CancellationToken cancellationToken)
        {
            await writeArtifacts(name, dto, cancellationToken);
            await extractGatewayApis(name, cancellationToken);
        }
    }

    private static void ConfigureListGateways(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListGateways);
    }

    private static ListGateways GetListGateways(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<GatewayName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(GatewayName, GatewayDto)> listFromSet(IEnumerable<GatewayName> names, CancellationToken cancellationToken) =>
            names.Select(name => GatewayUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 })
                 // Handle scenarios where the SKU doesn't support gateways
                 .Catch((HttpRequestException exception) =>
                            exception.StatusCode == HttpStatusCode.InternalServerError
                            && exception.Message.Contains("Request processing failed", StringComparison.OrdinalIgnoreCase)
                                ? AsyncEnumerable.Empty<(GatewayName, GatewayDto)>()
                                : throw exception);

        IAsyncEnumerable<(GatewayName, GatewayDto)> listAll(CancellationToken cancellationToken)
        {
            var gatewaysUri = GatewaysUri.From(serviceUri);
            return gatewaysUri.List(pipeline, cancellationToken);
        }
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