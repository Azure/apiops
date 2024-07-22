using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutTags(CancellationToken cancellationToken);
public delegate Option<TagName> TryParseTagName(FileInfo file);
public delegate bool IsTagNameInSourceControl(TagName name);
public delegate ValueTask PutTag(TagName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<TagDto>> FindTagDto(TagName name, CancellationToken cancellationToken);
public delegate ValueTask PutTagInApim(TagName name, TagDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteTags(CancellationToken cancellationToken);
public delegate ValueTask DeleteTag(TagName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteTagFromApim(TagName name, CancellationToken cancellationToken);

internal static class TagModule
{
    public static void ConfigurePutTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseTagName(builder);
        ConfigureIsTagNameInSourceControl(builder);
        ConfigurePutTag(builder);

        builder.Services.TryAddSingleton(GetPutTags);
    }

    private static PutTags GetPutTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsTagNameInSourceControl>();
        var put = provider.GetRequiredService<PutTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutTags));

            logger.LogInformation("Putting tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseTagName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseTagName);
    }

    private static TryParseTagName GetTryParseTagName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in TagInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsTagNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsTagNameInSourceControl);
    }

    private static IsTagNameInSourceControl GetIsTagNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(TagName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = TagInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutTag(IHostApplicationBuilder builder)
    {
        ConfigureFindTagDto(builder);
        ConfigurePutTagInApim(builder);

        builder.Services.TryAddSingleton(GetPutTag);
    }

    private static PutTag GetPutTag(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindTagDto>();
        var putInApim = provider.GetRequiredService<PutTagInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutTag))
                                       ?.AddTag("tag.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindTagDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindTagDto);
    }

    private static FindTagDto GetFindTagDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<TagName, TagDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = TagInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<TagDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutTagInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutTagInApim);
    }

    private static PutTagInApim GetPutTagInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting tag {TagName}...", name);

            await TagUri.From(name, serviceUri)
                        .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseTagName(builder);
        ConfigureIsTagNameInSourceControl(builder);
        ConfigureDeleteTag(builder);

        builder.Services.TryAddSingleton(GetDeleteTags);
    }

    private static DeleteTags GetDeleteTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsTagNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteTags));

            logger.LogInformation("Deleting tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteTag(IHostApplicationBuilder builder)
    {
        ConfigureDeleteTagFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteTag);
    }

    private static DeleteTag GetDeleteTag(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteTagFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteTag))
                                       ?.AddTag("tag.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteTagFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteTagFromApim);
    }

    private static DeleteTagFromApim GetDeleteTagFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting tag {TagName}...", name);

            await TagUri.From(name, serviceUri)
                        .Delete(pipeline, cancellationToken);
        };
    }
}