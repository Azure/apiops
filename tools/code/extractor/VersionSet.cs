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

internal delegate ValueTask ExtractVersionSets(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(VersionSetName Name, VersionSetDto Dto)> ListVersionSets(CancellationToken cancellationToken);

internal delegate bool ShouldExtractVersionSet(VersionSetName name);

file delegate ValueTask WriteVersionSetArtifacts(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteVersionSetInformationFile(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);

file sealed class ExtractVersionSetsHandler(ListVersionSets list, ShouldExtractVersionSet shouldExtract, WriteVersionSetArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(versionset => shouldExtract(versionset.Name))
                .IterParallel(async versionset => await writeArtifacts(versionset.Name, versionset.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListVersionSetsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(VersionSetName, VersionSetDto)> Handle(CancellationToken cancellationToken) =>
        VersionSetsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractVersionSetHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(VersionSetName name)
    {
        var shouldExtract = shouldExtractFactory.Create<VersionSetName>();
        return shouldExtract(name);
    }
}

file sealed class WriteVersionSetArtifactsHandler(WriteVersionSetInformationFile writeInformationFile)
{
    public async ValueTask Handle(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteVersionSetInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken)
    {
        var informationFile = VersionSetInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing version set information file {VersionSetInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class VersionSetServices
{
    public static void ConfigureExtractVersionSets(IServiceCollection services)
    {
        ConfigureListVersionSets(services);
        ConfigureShouldExtractVersionSet(services);
        ConfigureWriteVersionSetArtifacts(services);

        services.TryAddSingleton<ExtractVersionSetsHandler>();
        services.TryAddSingleton<ExtractVersionSets>(provider => provider.GetRequiredService<ExtractVersionSetsHandler>().Handle);
    }

    private static void ConfigureListVersionSets(IServiceCollection services)
    {
        services.TryAddSingleton<ListVersionSetsHandler>();
        services.TryAddSingleton<ListVersionSets>(provider => provider.GetRequiredService<ListVersionSetsHandler>().Handle);
    }

    public static void ConfigureShouldExtractVersionSet(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractVersionSetHandler>();
        services.TryAddSingleton<ShouldExtractVersionSet>(provider => provider.GetRequiredService<ShouldExtractVersionSetHandler>().Handle);
    }

    private static void ConfigureWriteVersionSetArtifacts(IServiceCollection services)
    {
        ConfigureWriteVersionSetInformationFile(services);

        services.TryAddSingleton<WriteVersionSetArtifactsHandler>();
        services.TryAddSingleton<WriteVersionSetArtifacts>(provider => provider.GetRequiredService<WriteVersionSetArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteVersionSetInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteVersionSetInformationFileHandler>();
        services.TryAddSingleton<WriteVersionSetInformationFile>(provider => provider.GetRequiredService<WriteVersionSetInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("VersionSetExtractor");
}