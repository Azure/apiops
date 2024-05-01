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

internal delegate ValueTask ExtractNamedValues(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(NamedValueName Name, NamedValueDto Dto)> ListNamedValues(CancellationToken cancellationToken);

file delegate bool ShouldExtractNamedValue(NamedValueName name);

file delegate ValueTask WriteNamedValueArtifacts(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteNamedValueInformationFile(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken);

file sealed class ExtractNamedValuesHandler(ListNamedValues list, ShouldExtractNamedValue shouldExtract, WriteNamedValueArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(namedvalue => shouldExtract(namedvalue.Name))
                .IterParallel(async namedvalue => await writeArtifacts(namedvalue.Name, namedvalue.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListNamedValuesHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(NamedValueName, NamedValueDto)> Handle(CancellationToken cancellationToken) =>
        NamedValuesUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractNamedValueHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(NamedValueName name)
    {
        var shouldExtract = shouldExtractFactory.Create<NamedValueName>();
        return shouldExtract(name);
    }
}

file sealed class WriteNamedValueArtifactsHandler(WriteNamedValueInformationFile writeInformationFile)
{
    public async ValueTask Handle(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteNamedValueInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken)
    {
        var informationFile = NamedValueInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing named value information file {NamedValueInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class NamedValueServices
{
    public static void ConfigureExtractNamedValues(IServiceCollection services)
    {
        ConfigureListNamedValues(services);
        ConfigureShouldExtractNamedValue(services);
        ConfigureWriteNamedValueArtifacts(services);

        services.TryAddSingleton<ExtractNamedValuesHandler>();
        services.TryAddSingleton<ExtractNamedValues>(provider => provider.GetRequiredService<ExtractNamedValuesHandler>().Handle);
    }

    private static void ConfigureListNamedValues(IServiceCollection services)
    {
        services.TryAddSingleton<ListNamedValuesHandler>();
        services.TryAddSingleton<ListNamedValues>(provider => provider.GetRequiredService<ListNamedValuesHandler>().Handle);
    }

    private static void ConfigureShouldExtractNamedValue(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractNamedValueHandler>();
        services.TryAddSingleton<ShouldExtractNamedValue>(provider => provider.GetRequiredService<ShouldExtractNamedValueHandler>().Handle);
    }

    private static void ConfigureWriteNamedValueArtifacts(IServiceCollection services)
    {
        ConfigureWriteNamedValueInformationFile(services);

        services.TryAddSingleton<WriteNamedValueArtifactsHandler>();
        services.TryAddSingleton<WriteNamedValueArtifacts>(provider => provider.GetRequiredService<WriteNamedValueArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteNamedValueInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteNamedValueInformationFileHandler>();
        services.TryAddSingleton<WriteNamedValueInformationFile>(provider => provider.GetRequiredService<WriteNamedValueInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("NamedValueExtractor");
}