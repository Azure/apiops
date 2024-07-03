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

internal delegate ValueTask ExtractTags(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(TagName Name, TagDto Dto)> ListTags(CancellationToken cancellationToken);

internal delegate bool ShouldExtractTag(TagName name);

file delegate ValueTask WriteTagArtifacts(TagName name, TagDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteTagInformationFile(TagName name, TagDto dto, CancellationToken cancellationToken);

file sealed class ExtractTagsHandler(ListTags list, ShouldExtractTag shouldExtract, WriteTagArtifacts writeArtifacts)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(tag => shouldExtract(tag.Name))
                .IterParallel(async tag => await writeArtifacts(tag.Name, tag.Dto, cancellationToken),
                              cancellationToken);
}

file sealed class ListTagsHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline)
{
    public IAsyncEnumerable<(TagName, TagDto)> Handle(CancellationToken cancellationToken) =>
        TagsUri.From(serviceUri).List(pipeline, cancellationToken);
}

file sealed class ShouldExtractTagHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(TagName name)
    {
        var shouldExtract = shouldExtractFactory.Create<TagName>();
        return shouldExtract(name);
    }
}

file sealed class WriteTagArtifactsHandler(WriteTagInformationFile writeInformationFile)
{
    public async ValueTask Handle(TagName name, TagDto dto, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);
    }
}

file sealed class WriteTagInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(TagName name, TagDto dto, CancellationToken cancellationToken)
    {
        var informationFile = TagInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing tag information file {InformationFile}", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

internal static class TagServices
{
    public static void ConfigureExtractTags(IServiceCollection services)
    {
        ConfigureListTags(services);
        ConfigureShouldExtractTag(services);
        ConfigureWriteTagArtifacts(services);

        services.TryAddSingleton<ExtractTagsHandler>();
        services.TryAddSingleton<ExtractTags>(provider => provider.GetRequiredService<ExtractTagsHandler>().Handle);
    }

    private static void ConfigureListTags(IServiceCollection services)
    {
        services.TryAddSingleton<ListTagsHandler>();
        services.TryAddSingleton<ListTags>(provider => provider.GetRequiredService<ListTagsHandler>().Handle);
    }

    public static void ConfigureShouldExtractTag(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractTagHandler>();
        services.TryAddSingleton<ShouldExtractTag>(provider => provider.GetRequiredService<ShouldExtractTagHandler>().Handle);
    }

    private static void ConfigureWriteTagArtifacts(IServiceCollection services)
    {
        ConfigureWriteTagInformationFile(services);

        services.TryAddSingleton<WriteTagArtifactsHandler>();
        services.TryAddSingleton<WriteTagArtifacts>(provider => provider.GetRequiredService<WriteTagArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteTagInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteTagInformationFileHandler>();
        services.TryAddSingleton<WriteTagInformationFile>(provider => provider.GetRequiredService<WriteTagInformationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger("TagExtractor");
}