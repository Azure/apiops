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

public delegate ValueTask PutSubscriptions(CancellationToken cancellationToken);
public delegate Option<SubscriptionName> TryParseSubscriptionName(FileInfo file);
public delegate bool IsSubscriptionNameInSourceControl(SubscriptionName name);
public delegate ValueTask PutSubscription(SubscriptionName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<SubscriptionDto>> FindSubscriptionDto(SubscriptionName name, CancellationToken cancellationToken);
public delegate ValueTask PutSubscriptionInApim(SubscriptionName name, SubscriptionDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteSubscriptions(CancellationToken cancellationToken);
public delegate ValueTask DeleteSubscription(SubscriptionName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteSubscriptionFromApim(SubscriptionName name, CancellationToken cancellationToken);

internal static class SubscriptionModule
{
    public static void ConfigurePutSubscriptions(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseSubscriptionName(builder);
        ConfigureIsSubscriptionNameInSourceControl(builder);
        ConfigurePutSubscription(builder);

        builder.Services.TryAddSingleton(GetPutSubscriptions);
    }

    private static PutSubscriptions GetPutSubscriptions(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseSubscriptionName>();
        var isNameInSourceControl = provider.GetRequiredService<IsSubscriptionNameInSourceControl>();
        var put = provider.GetRequiredService<PutSubscription>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutSubscriptions));

            logger.LogInformation("Putting subscriptions...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseSubscriptionName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseSubscriptionName);
    }

    private static TryParseSubscriptionName GetTryParseSubscriptionName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in SubscriptionInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsSubscriptionNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsSubscriptionNameInSourceControl);
    }

    private static IsSubscriptionNameInSourceControl GetIsSubscriptionNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(SubscriptionName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = SubscriptionInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutSubscription(IHostApplicationBuilder builder)
    {
        ConfigureFindSubscriptionDto(builder);
        ConfigurePutSubscriptionInApim(builder);

        builder.Services.TryAddSingleton(GetPutSubscription);
    }

    private static PutSubscription GetPutSubscription(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindSubscriptionDto>();
        var putInApim = provider.GetRequiredService<PutSubscriptionInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutSubscription))
                                       ?.AddTag("subscription.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindSubscriptionDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindSubscriptionDto);
    }

    private static FindSubscriptionDto GetFindSubscriptionDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<SubscriptionName, SubscriptionDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = SubscriptionInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<SubscriptionDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutSubscriptionInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutSubscriptionInApim);
    }

    private static PutSubscriptionInApim GetPutSubscriptionInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting subscription {SubscriptionName}...", name);

            await SubscriptionUri.From(name, serviceUri)
                                 .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteSubscriptions(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseSubscriptionName(builder);
        ConfigureIsSubscriptionNameInSourceControl(builder);
        ConfigureDeleteSubscription(builder);

        builder.Services.TryAddSingleton(GetDeleteSubscriptions);
    }

    private static DeleteSubscriptions GetDeleteSubscriptions(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseSubscriptionName>();
        var isNameInSourceControl = provider.GetRequiredService<IsSubscriptionNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteSubscription>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteSubscriptions));

            logger.LogInformation("Deleting subscriptions...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteSubscription(IHostApplicationBuilder builder)
    {
        ConfigureDeleteSubscriptionFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteSubscription);
    }

    private static DeleteSubscription GetDeleteSubscription(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteSubscriptionFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteSubscription))
                                       ?.AddTag("subscription.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteSubscriptionFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteSubscriptionFromApim);
    }

    private static DeleteSubscriptionFromApim GetDeleteSubscriptionFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting subscription {SubscriptionName}...", name);

            await SubscriptionUri.From(name, serviceUri)
                                 .Delete(pipeline, cancellationToken);
        };
    }
}