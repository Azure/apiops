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

internal delegate ValueTask ExtractBackends(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(BackendName Name, BackendDto Dto)> ListBackends(CancellationToken cancellationToken);

file delegate bool ShouldExtractBackend(BackendName name);

file delegate ValueTask WriteBackendArtifacts(BackendName name, BackendDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteBackendInformationFile(BackendName name, BackendDto dto, CancellationToken cancellationToken);

file sealed class ExtractBackendsHandler(ListBackends list, ShouldExtractBackend shouldExtract, WriteBackendArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(backend => shouldExtract(backend.Name))
                .IterParallel(async backend => await writeArtifacts(backend.Name, backend.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListBackendsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(BackendName, BackendDto)> Handle(CancellationToken cancellationToken) =>
        BackendsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractBackendHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(BackendName name)
    {
        var shouldExtract = shouldExtractFactory.Create<BackendName>();
        return shouldExtract(name);
    }
}

file sealed class WriteBackendArtifactsHandler(WriteBackendInformationFile writeInformationFile)
{
    public async ValueTask Handle(BackendName name, BackendDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteBackendInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(BackendName name, BackendDto dto, CancellationToken cancellationToken)
    {
        var informationFile = BackendInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing backend information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class BackendServices
{
    public static void ConfigureExtractBackends(IServiceCollection services)
    {
        ConfigureListBackends(services);
        ConfigureShouldExtractBackend(services);
        ConfigureWriteBackendArtifacts(services);

        services.TryAddSingleton<ExtractBackendsHandler>();
        services.TryAddSingleton<ExtractBackends>(provider => provider.GetRequiredService<ExtractBackendsHandler>().Handle);
    }

    private static void ConfigureListBackends(IServiceCollection services)
    {
        services.TryAddSingleton<ListBackendsHandler>();
        services.TryAddSingleton<ListBackends>(provider => provider.GetRequiredService<ListBackendsHandler>().Handle);
    }

    private static void ConfigureShouldExtractBackend(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractBackendHandler>();
        services.TryAddSingleton<ShouldExtractBackend>(provider => provider.GetRequiredService<ShouldExtractBackendHandler>().Handle);
    }

    private static void ConfigureWriteBackendArtifacts(IServiceCollection services)
    {
        ConfigureWriteBackendInformationFile(services);

        services.TryAddSingleton<WriteBackendArtifactsHandler>();
        services.TryAddSingleton<WriteBackendArtifacts>(provider => provider.GetRequiredService<WriteBackendArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteBackendInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteBackendInformationFileHandler>();
        services.TryAddSingleton<WriteBackendInformationFile>(provider => provider.GetRequiredService<WriteBackendInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("BackendExtractor");
}