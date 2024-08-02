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

public delegate ValueTask ExtractTags(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(TagName Name, TagDto Dto)> ListTags(CancellationToken cancellationToken);
public delegate ValueTask WriteTagArtifacts(TagName name, TagDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteTagInformationFile(TagName name, TagDto dto, CancellationToken cancellationToken);

internal static class TagModule
{
    public static void ConfigureExtractTags(IHostApplicationBuilder builder)
    {
        ConfigureListTags(builder);
        ConfigureWriteTagArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractTags);
    }

    private static ExtractTags GetExtractTags(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListTags>();
        var writeArtifacts = provider.GetRequiredService<WriteTagArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractTags));

            logger.LogInformation("Extracting tags...");

            await list(cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListTags(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListTags);
    }

    private static ListTags GetListTags(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationNames = findConfigurationNamesFactory.Create<TagName>();

        return cancellationToken =>
            findConfigurationNames()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(TagName, TagDto)> listFromSet(IEnumerable<TagName> names, CancellationToken cancellationToken) =>
            names.Select(name => TagUri.From(name, serviceUri))
                 .ToAsyncEnumerable()
                 .Choose(async uri =>
                 {
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.Map(dto => (uri.Name, dto));
                 });

        IAsyncEnumerable<(TagName, TagDto)> listAll(CancellationToken cancellationToken)
        {
            var tagsUri = TagsUri.From(serviceUri);
            return tagsUri.List(pipeline, cancellationToken);
        }
    }

    private static void ConfigureWriteTagArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteTagInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteTagArtifacts);
    }

    private static WriteTagArtifacts GetWriteTagArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteTagInformationFile>();

        return async (name, dto, cancellationToken) =>
            await writeInformationFile(name, dto, cancellationToken);
    }

    private static void ConfigureWriteTagInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteTagInformationFile);
    }

    private static WriteTagInformationFile GetWriteTagInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = TagInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing tag information file {TagInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}