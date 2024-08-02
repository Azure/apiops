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

public delegate ValueTask ExtractVersionSets(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(VersionSetName Name, VersionSetDto Dto)> ListVersionSets(CancellationToken cancellationToken);
public delegate ValueTask WriteVersionSetArtifacts(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteVersionSetInformationFile(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);

internal static class VersionSetModule
{
    public static void ConfigureExtractVersionSets(IHostApplicationBuilder builder)
    {
        ConfigureListVersionSets(builder);
        ConfigureWriteVersionSetArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractVersionSets);
    }

    private static ExtractVersionSets GetExtractVersionSets(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListVersionSets>();
        var writeArtifacts = provider.GetRequiredService<WriteVersionSetArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractVersionSets));

            logger.LogInformation("Extracting version sets...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListVersionSets(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListVersionSets);
    }

    private static ListVersionSets GetListVersionSets(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<VersionSetName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(VersionSetName, VersionSetDto)> listFromSet(IEnumerable<VersionSetName> names, CancellationToken cancellationToken) =>
            names.Select(name => VersionSetUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(VersionSetName, VersionSetDto)> listAll(CancellationToken cancellationToken)
        {
            var versionSetsUri = VersionSetsUri.From(serviceUri);
            return versionSetsUri.List(pipeline, cancellationToken);
        }
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