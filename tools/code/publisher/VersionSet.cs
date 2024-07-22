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

public delegate ValueTask PutVersionSets(CancellationToken cancellationToken);
public delegate Option<VersionSetName> TryParseVersionSetName(FileInfo file);
public delegate bool IsVersionSetNameInSourceControl(VersionSetName name);
public delegate ValueTask PutVersionSet(VersionSetName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<VersionSetDto>> FindVersionSetDto(VersionSetName name, CancellationToken cancellationToken);
public delegate ValueTask PutVersionSetInApim(VersionSetName name, VersionSetDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteVersionSets(CancellationToken cancellationToken);
public delegate ValueTask DeleteVersionSet(VersionSetName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteVersionSetFromApim(VersionSetName name, CancellationToken cancellationToken);

internal static class VersionSetModule
{
    public static void ConfigurePutVersionSets(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseVersionSetName(builder);
        ConfigureIsVersionSetNameInSourceControl(builder);
        ConfigurePutVersionSet(builder);

        builder.Services.TryAddSingleton(GetPutVersionSets);
    }

    private static PutVersionSets GetPutVersionSets(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseVersionSetName>();
        var isNameInSourceControl = provider.GetRequiredService<IsVersionSetNameInSourceControl>();
        var put = provider.GetRequiredService<PutVersionSet>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutVersionSets));

            logger.LogInformation("Putting version sets...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseVersionSetName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseVersionSetName);
    }

    private static TryParseVersionSetName GetTryParseVersionSetName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in VersionSetInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsVersionSetNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsVersionSetNameInSourceControl);
    }

    private static IsVersionSetNameInSourceControl GetIsVersionSetNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(VersionSetName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = VersionSetInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutVersionSet(IHostApplicationBuilder builder)
    {
        ConfigureFindVersionSetDto(builder);
        ConfigurePutVersionSetInApim(builder);

        builder.Services.TryAddSingleton(GetPutVersionSet);
    }

    private static PutVersionSet GetPutVersionSet(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindVersionSetDto>();
        var putInApim = provider.GetRequiredService<PutVersionSetInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutVersionSet))
                                       ?.AddTag("version_set.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindVersionSetDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindVersionSetDto);
    }

    private static FindVersionSetDto GetFindVersionSetDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<VersionSetName, VersionSetDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = VersionSetInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<VersionSetDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutVersionSetInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutVersionSetInApim);
    }

    private static PutVersionSetInApim GetPutVersionSetInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting version set {VersionSetName}...", name);

            await VersionSetUri.From(name, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteVersionSets(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseVersionSetName(builder);
        ConfigureIsVersionSetNameInSourceControl(builder);
        ConfigureDeleteVersionSet(builder);

        builder.Services.TryAddSingleton(GetDeleteVersionSets);
    }

    private static DeleteVersionSets GetDeleteVersionSets(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseVersionSetName>();
        var isNameInSourceControl = provider.GetRequiredService<IsVersionSetNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteVersionSet>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteVersionSets));

            logger.LogInformation("Deleting version sets...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteVersionSet(IHostApplicationBuilder builder)
    {
        ConfigureDeleteVersionSetFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteVersionSet);
    }

    private static DeleteVersionSet GetDeleteVersionSet(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteVersionSetFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteVersionSet))
                                       ?.AddTag("version_set.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteVersionSetFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteVersionSetFromApim);
    }

    private static DeleteVersionSetFromApim GetDeleteVersionSetFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting version set {VersionSetName}...", name);

            await VersionSetUri.From(name, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}