using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractGateways(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(GatewayName Name, GatewayDto Dto)> ListGateways(CancellationToken cancellationToken);

file delegate bool ShouldExtractGateway(GatewayName name);

file delegate ValueTask WriteGatewayArtifacts(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteGatewayInformationFile(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);

file sealed class ExtractGatewaysHandler(ListGateways list,
                                         ShouldExtractGateway shouldExtract,
                                         WriteGatewayArtifacts writeArtifacts,
                                         ExtractGatewayApis extractGatewayApis)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(gateway => shouldExtract(gateway.Name))
                .IterParallel(async gateway => await ExtractGateway(gateway.Name, gateway.Dto, cancellationToken),
                              cancellationToken);

    private async ValueTask ExtractGateway(GatewayName name, GatewayDto dto, CancellationToken cancellationToken)
    {
        await writeArtifacts(name, dto, cancellationToken);
        await extractGatewayApis(name, cancellationToken);
    }
}

file sealed class ListGatewaysHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(GatewayName, GatewayDto)> Handle(CancellationToken cancellationToken) =>
        GatewaysUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractGatewayHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(GatewayName name)
    {
        var shouldExtract = shouldExtractFactory.Create<GatewayName>();
        return shouldExtract(name);
    }
}

file sealed class WriteGatewayArtifactsHandler(WriteGatewayInformationFile writeInformationFile)
{
    public async ValueTask Handle(GatewayName name, GatewayDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteGatewayInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(GatewayName name, GatewayDto dto, CancellationToken cancellationToken)
    {
        var informationFile = GatewayInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing gateway information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class GatewayServices
{
    public static void ConfigureExtractGateways(IServiceCollection services)
    {
        ConfigureListGateways(services);
        ConfigureShouldExtractGateway(services);
        ConfigureWriteGatewayArtifacts(services);
        GatewayApiServices.ConfigureExtractGatewayApis(services);

        services.TryAddSingleton<ExtractGatewaysHandler>();
        services.TryAddSingleton<ExtractGateways>(provider => provider.GetRequiredService<ExtractGatewaysHandler>().Handle);
    }

    private static void ConfigureListGateways(IServiceCollection services)
    {
        services.TryAddSingleton<ListGatewaysHandler>();
        services.TryAddSingleton<ListGateways>(provider => provider.GetRequiredService<ListGatewaysHandler>().Handle);
    }

    private static void ConfigureShouldExtractGateway(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractGatewayHandler>();
        services.TryAddSingleton<ShouldExtractGateway>(provider => provider.GetRequiredService<ShouldExtractGatewayHandler>().Handle);
    }

    private static void ConfigureWriteGatewayArtifacts(IServiceCollection services)
    {
        ConfigureWriteGatewayInformationFile(services);

        services.TryAddSingleton<WriteGatewayArtifactsHandler>();
        services.TryAddSingleton<WriteGatewayArtifacts>(provider => provider.GetRequiredService<WriteGatewayArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteGatewayInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteGatewayInformationFileHandler>();
        services.TryAddSingleton<WriteGatewayInformationFile>(provider => provider.GetRequiredService<WriteGatewayInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("GatewayExtractor");
}