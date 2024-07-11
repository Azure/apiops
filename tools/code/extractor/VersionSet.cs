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

public delegate ValueTask ExtractVersionSets(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(VersionSetName Name, VersionSetDto Dto)> ListVersionSets(CancellationToken cancellationToken);
public delegate bool ShouldExtractVersionSet(VersionSetName name);
public delegate ValueTask WriteVersionSetArtifacts(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteVersionSetInformationFile(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);

internal static class VersionSetModule
{
    public static void ConfigureExtractVersionSets(IHostApplicationBuilder builder)
    {
        ConfigureListVersionSets(builder);
        ConfigureShouldExtractVersionSet(builder);
        ConfigureWriteVersionSetArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractVersionSets);
    }

    private static ExtractVersionSets GetExtractVersionSets(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListVersionSets>();
        var shouldExtract = provider.GetRequiredService<ShouldExtractVersionSet>();
        var writeArtifacts = provider.GetRequiredService<WriteVersionSetArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractVersionSets));

            logger.LogInformation("Extracting version sets...");

            await list(cancellationToken)
                    .Where(versionset => shouldExtract(versionset.Name))
                    .IterParallel(async versionset => await writeArtifacts(versionset.Name, versionset.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListVersionSets(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListVersionSets);
    }

    private static ListVersionSets GetListVersionSets(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return cancellationToken =>
            VersionSetsUri.From(serviceUri)
                          .List(pipeline, cancellationToken);
    }

    private static void ConfigureShouldExtractVersionSet(IHostApplicationBuilder builder)
    {
        ShouldExtractModule.ConfigureShouldExtractFactory(builder);

        builder.Services.TryAddSingleton(GetShouldExtractVersionSet);
    }

    private static ShouldExtractVersionSet GetShouldExtractVersionSet(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<VersionSetName>();

        return name => shouldExtract(name);
    }

    private static void ConfigureWriteVersionSetArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteVersionSetInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteVersionSetArtifacts);
    }

    private static WriteVersionSetArtifacts GetWriteVersionSetArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteVersionSetInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteVersionSetInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteVersionSetInformationFile);
    }

    private static WriteVersionSetInformationFile GetWriteVersionSetInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = VersionSetInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing version set information file {VersionSetInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}