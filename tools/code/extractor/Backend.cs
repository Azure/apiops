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

public delegate ValueTask ExtractBackends(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(BackendName Name, BackendDto Dto)> ListBackends(CancellationToken cancellationToken);
public delegate bool ShouldExtractBackend(BackendName name);
public delegate ValueTask WriteBackendArtifacts(BackendName name, BackendDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteBackendInformationFile(BackendName name, BackendDto dto, CancellationToken cancellationToken);

internal static class BackendModule
{
    public static void ConfigureExtractBackends(IHostApplicationBuilder builder)
    {
        ConfigureListBackends(builder);
        ConfigureShouldExtractBackend(builder);
        ConfigureWriteBackendArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractBackends);
    }

    private static ExtractBackends GetExtractBackends(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListBackends>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractBackend>();
        var writeArtifacts = provider.GetRequiredService<WriteBackendArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractBackends));

            logger.LogInformation("Extracting backends...");

            await list(cancellationToken)
                    .Where(resource => shouldExtract(resource.Name))
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListBackends(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListBackends);
    }

    private static ListBackends GetListBackends(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
        {
            var backendsUri = BackendsUri.From(serviceUri);
            return backendsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureShouldExtractBackend(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractBackend);
    }

    private static ShouldExtractBackend GetShouldExtractBackend(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<BackendName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteBackendArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteBackendInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteBackendArtifacts);
    }

    private static WriteBackendArtifacts GetWriteBackendArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteBackendInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteBackendInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteBackendInformationFile);
    }

    private static WriteBackendInformationFile GetWriteBackendInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = BackendInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing backend information file {BackendInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}