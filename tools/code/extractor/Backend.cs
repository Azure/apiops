using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractBackends(CancellationToken cancellationToken);

internal delegate IAsyncEnumerable<(BackendName Name, BackendDto Dto)> ListBackends(CancellationToken cancellationToken);

internal delegate bool ShouldExtractBackend(BackendName name);

internal delegate ValueTask WriteBackendArtifacts(BackendName name, BackendDto dto, CancellationToken cancellationToken);

internal delegate ValueTask WriteBackendInformationFile(BackendName name, BackendDto dto, CancellationToken cancellationToken);

internal static class BackendServices
{
    public static void ConfigureExtractBackends(IServiceCollection services)
    {
        ConfigureListBackends(services);
        ConfigureShouldExtractBackend(services);
        ConfigureWriteBackendArtifacts(services);

        services.TryAddSingleton(ExtractBackends);
    }

    private static ExtractBackends ExtractBackends(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListBackends>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractBackend>();
        var writeArtifacts = provider.GetRequiredService<WriteBackendArtifacts>();

        return async cancellationToken =>
            await list(cancellationToken)
                    .Where(backend => shouldExtract(backend.Name))
                    .IterParallel(async backend => await writeArtifacts(backend.Name, backend.Dto, cancellationToken),
                                  cancellationToken);
    }

    private static void ConfigureListBackends(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListBackends);
    }

    private static ListBackends ListBackends(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            BackendsUri.From(serviceUri)
                       .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractBackend(IServiceCollection services)
    {
        CommonServices.ConfigureShouldExtractFactory(services);

        services.TryAddSingleton(ShouldExtractBackend);
    }

    private static ShouldExtractBackend ShouldExtractBackend(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<BackendName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteBackendArtifacts(IServiceCollection services)
    {
        ConfigureWriteBackendInformationFile(services);

        services.TryAddSingleton(WriteBackendArtifacts);
    }

    private static WriteBackendArtifacts WriteBackendArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteBackendInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteBackendInformationFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteBackendInformationFile);
    }

    private static WriteBackendInformationFile WriteBackendInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = BackendInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing backend information file {BackendInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("BackendExtractor");
}