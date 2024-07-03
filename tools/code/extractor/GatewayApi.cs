using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractGatewayApis(GatewayName gatewayName, CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ApiName Name, GatewayApiDto Dto)> ListGatewayApis(GatewayName gatewayName, CancellationToken cancellationToken);

file delegate ValueTask WriteGatewayApiArtifacts(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken);

file delegate ValueTask WriteGatewayApiInformationFile(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken);

file sealed class ExtractGatewayApisHandler(ListGatewayApis list, ShouldExtractApiName shouldExtractApi, WriteGatewayApiArtifacts writeArtifacts)
{
    public async ValueTask Handle(GatewayName gatewayName, CancellationToken cancellationToken) =>
        await list(gatewayName, cancellationToken)
                .Where(api => shouldExtractApi(api.Name))
                .IterParallel(async gatewayapi => await writeArtifacts(gatewayapi.Name, gatewayapi.Dto, gatewayName, cancellationToken),
                              cancellationToken);
}

file sealed class ListGatewayApisHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(ApiName, GatewayApiDto)> Handle(GatewayName gatewayName, CancellationToken cancellationToken) =>
        GatewayApisUri.From(gatewayName, serviceUri).List(pipeline, cancellationToken);
}

file sealed class WriteGatewayApiArtifactsHandler(WriteGatewayApiInformationFile writeApiFile)
{
    public async ValueTask Handle(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        await writeApiFile(name, dto, gatewayName, cancellationToken);
    }
}

file sealed class WriteGatewayApiInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken)
    {
        var informationFile = GatewayApiInformationFile.From(name, gatewayName, serviceDirectory);

        logger.LogInformation("Writing gateway api information file {GatewayApiInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class GatewayApiServices
{
    public static void ConfigureExtractGatewayApis(IServiceCollection services)
    {
        ConfigureListGatewayApis(services);
        ApiServices.ConfigureShouldExtractApiName(services);
        ConfigureWriteGatewayApiArtifacts(services);

        services.TryAddSingleton<ExtractGatewayApisHandler>();
        services.TryAddSingleton<ExtractGatewayApis>(provider => provider.GetRequiredService<ExtractGatewayApisHandler>().Handle);
    }

    private static void ConfigureListGatewayApis(IServiceCollection services)
    {
        services.TryAddSingleton<ListGatewayApisHandler>();
        services.TryAddSingleton<ListGatewayApis>(provider => provider.GetRequiredService<ListGatewayApisHandler>().Handle);
    }

    private static void ConfigureWriteGatewayApiArtifacts(IServiceCollection services)
    {
        ConfigureWriteGatewayApiInformationFile(services);

        services.TryAddSingleton<WriteGatewayApiArtifactsHandler>();
        services.TryAddSingleton<WriteGatewayApiArtifacts>(provider => provider.GetRequiredService<WriteGatewayApiArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteGatewayApiInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteGatewayApiInformationFileHandler>();
        services.TryAddSingleton<WriteGatewayApiInformationFile>(provider => provider.GetRequiredService<WriteGatewayApiInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("GatewayApiExtractor");
}