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

public delegate ValueTask PutApiTags(CancellationToken cancellationToken);
public delegate Option<(TagName Name, ApiName ApiName)> TryParseApiTagName(FileInfo file);
public delegate bool IsApiTagNameInSourceControl(TagName name, ApiName apiName);
public delegate ValueTask PutApiTag(TagName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ApiTagDto>> FindApiTagDto(TagName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask PutApiTagInApim(TagName name, ApiTagDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiTags(CancellationToken cancellationToken);
public delegate ValueTask DeleteApiTag(TagName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiTagFromApim(TagName name, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiTagModule
{
    public static void ConfigurePutApiTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryApiParseTagName(builder);
        ConfigureIsApiTagNameInSourceControl(builder);
        ConfigurePutApiTag(builder);

        builder.Services.TryAddSingleton(GetPutApiTags);
    }

    private static PutApiTags GetPutApiTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiTagNameInSourceControl>();
        var put = provider.GetRequiredService<PutApiTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiTags));

            logger.LogInformation("Putting API tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.ApiName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryApiParseTagName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseApiTagName);
    }

    private static TryParseApiTagName GetTryParseApiTagName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in ApiTagInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsApiTagNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsTagNameInSourceControl);
    }

    private static IsApiTagNameInSourceControl GetIsTagNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(TagName name, ApiName apiName)
        {
            var artifactFiles = getArtifactFiles();
            var tagFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);

            return artifactFiles.Contains(tagFile.ToFileInfo());
        }
    }

    private static void ConfigurePutApiTag(IHostApplicationBuilder builder)
    {
        ConfigureFindApiTagDto(builder);
        ConfigurePutApiTagInApim(builder);

        builder.Services.TryAddSingleton(GetPutApiTag);
    }

    private static PutApiTag GetPutApiTag(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindApiTagDto>();
        var putInApim = provider.GetRequiredService<PutApiTagInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiTag))
                                       ?.AddTag("api_tag.name", name)
                                       ?.AddTag("api.name", apiName);

            var dtoOption = await findDto(name, apiName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, apiName, cancellationToken));
        };
    }

    private static void ConfigureFindApiTagDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindApiTagDto);
    }

    private static FindApiTagDto GetFindApiTagDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, apiName, cancellationToken) =>
        {
            var informationFile = ApiTagInformationFile.From(name, apiName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<ApiTagDto>();
        };
    }

    private static void ConfigurePutApiTagInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiTagInApim);
    }

    private static PutApiTagInApim GetPutApiTagInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            logger.LogInformation("Adding tag {TagName} to API {ApiName}...", name, apiName);

            await ApiTagUri.From(name, apiName, serviceUri)
                           .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteApiTags(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryApiParseTagName(builder);
        ConfigureIsApiTagNameInSourceControl(builder);
        ConfigureDeleteApiTag(builder);

        builder.Services.TryAddSingleton(GetDeleteApiTags);
    }

    private static DeleteApiTags GetDeleteApiTags(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiTagName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiTagNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteApiTag>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiTags));

            logger.LogInformation("Deleting API tags...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.ApiName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiTag(IHostApplicationBuilder builder)
    {
        ConfigureDeleteApiTagFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteApiTag);
    }

    private static DeleteApiTag GetDeleteApiTag(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteApiTagFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiTag))
                                       ?.AddTag("api_tag.name", name)
                                       ?.AddTag("api.name", apiName);

            await deleteFromApim(name, apiName, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiTagFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApiTagFromApim);
    }

    private static DeleteApiTagFromApim GetDeleteApiTagFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, apiName, cancellationToken) =>
        {
            logger.LogInformation("Removing tag {TagName} from API {ApiName}...", name, apiName);

            await ApiTagUri.From(name, apiName, serviceUri)
                           .Delete(pipeline, cancellationToken);
        };
    }
}