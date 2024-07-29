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

public delegate ValueTask PutGroups(CancellationToken cancellationToken);
public delegate Option<GroupName> TryParseGroupName(FileInfo file);
public delegate bool IsGroupNameInSourceControl(GroupName name);
public delegate ValueTask PutGroup(GroupName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<GroupDto>> FindGroupDto(GroupName name, CancellationToken cancellationToken);
public delegate ValueTask PutGroupInApim(GroupName name, GroupDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteGroups(CancellationToken cancellationToken);
public delegate ValueTask DeleteGroup(GroupName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteGroupFromApim(GroupName name, CancellationToken cancellationToken);

internal static class GroupModule
{
    public static void ConfigurePutGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseGroupName(builder);
        ConfigureIsGroupNameInSourceControl(builder);
        ConfigurePutGroup(builder);

        builder.Services.TryAddSingleton(GetPutGroups);
    }

    private static PutGroups GetPutGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsGroupNameInSourceControl>();
        var put = provider.GetRequiredService<PutGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGroups));

            logger.LogInformation("Putting groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseGroupName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseGroupName);
    }

    private static TryParseGroupName GetTryParseGroupName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in GroupInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsGroupNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsGroupNameInSourceControl);
    }

    private static IsGroupNameInSourceControl GetIsGroupNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(GroupName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = GroupInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutGroup(IHostApplicationBuilder builder)
    {
        ConfigureFindGroupDto(builder);
        ConfigurePutGroupInApim(builder);

        builder.Services.TryAddSingleton(GetPutGroup);
    }

    private static PutGroup GetPutGroup(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindGroupDto>();
        var putInApim = provider.GetRequiredService<PutGroupInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGroup))
                                       ?.AddTag("group.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindGroupDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindGroupDto);
    }

    private static FindGroupDto GetFindGroupDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<GroupName, GroupDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = GroupInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<GroupDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutGroupInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutGroupInApim);
    }

    private static PutGroupInApim GetPutGroupInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting group {GroupName}...", name);

            await GroupUri.From(name, serviceUri)
                          .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteGroups(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseGroupName(builder);
        ConfigureIsGroupNameInSourceControl(builder);
        ConfigureDeleteGroup(builder);

        builder.Services.TryAddSingleton(GetDeleteGroups);
    }

    private static DeleteGroups GetDeleteGroups(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseGroupName>();
        var isNameInSourceControl = provider.GetRequiredService<IsGroupNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteGroup>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteGroups));

            logger.LogInformation("Deleting groups...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteGroup(IHostApplicationBuilder builder)
    {
        ConfigureDeleteGroupFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteGroup);
    }

    private static DeleteGroup GetDeleteGroup(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteGroupFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteGroup))
                                       ?.AddTag("group.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteGroupFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteGroupFromApim);
    }

    private static DeleteGroupFromApim GetDeleteGroupFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting group {GroupName}...", name);

            await GroupUri.From(name, serviceUri)
                          .Delete(pipeline, cancellationToken);
        };
    }
}