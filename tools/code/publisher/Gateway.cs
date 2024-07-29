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

public delegate ValueTask PutGateways(CancellationToken cancellationToken);
public delegate Option<GatewayName> TryParseGatewayName(FileInfo file);
public delegate bool IsGatewayNameInSourceControl(GatewayName name);
public delegate ValueTask PutGateway(GatewayName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<GatewayDto>> FindGatewayDto(GatewayName name, CancellationToken cancellationToken);
public delegate ValueTask PutGatewayInApim(GatewayName name, GatewayDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteGateways(CancellationToken cancellationToken);
public delegate ValueTask DeleteGateway(GatewayName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteGatewayFromApim(GatewayName name, CancellationToken cancellationToken);

internal static class GatewayModule
{
    public static void ConfigurePutGateways(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseGatewayName(builder);
        ConfigureIsGatewayNameInSourceControl(builder);
        ConfigurePutGateway(builder);

        builder.Services.TryAddSingleton(GetPutGateways);
    }

    private static PutGateways GetPutGateways(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseGatewayName>();
        var isNameInSourceControl = provider.GetRequiredService<IsGatewayNameInSourceControl>();
        var put = provider.GetRequiredService<PutGateway>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGateways));

            logger.LogInformation("Putting gateways...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseGatewayName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseGatewayName);
    }

    private static TryParseGatewayName GetTryParseGatewayName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in GatewayInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsGatewayNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsGatewayNameInSourceControl);
    }

    private static IsGatewayNameInSourceControl GetIsGatewayNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(GatewayName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = GatewayInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutGateway(IHostApplicationBuilder builder)
    {
        ConfigureFindGatewayDto(builder);
        ConfigurePutGatewayInApim(builder);

        builder.Services.TryAddSingleton(GetPutGateway);
    }

    private static PutGateway GetPutGateway(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindGatewayDto>();
        var putInApim = provider.GetRequiredService<PutGatewayInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGateway))
                                       ?.AddTag("gateway.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindGatewayDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindGatewayDto);
    }

    private static FindGatewayDto GetFindGatewayDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<GatewayName, GatewayDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = GatewayInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<GatewayDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutGatewayInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutGatewayInApim);
    }

    private static PutGatewayInApim GetPutGatewayInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting gateway {GatewayName}...", name);

            await GatewayUri.From(name, serviceUri)
                            .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteGateways(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseGatewayName(builder);
        ConfigureIsGatewayNameInSourceControl(builder);
        ConfigureDeleteGateway(builder);

        builder.Services.TryAddSingleton(GetDeleteGateways);
    }

    private static DeleteGateways GetDeleteGateways(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseGatewayName>();
        var isNameInSourceControl = provider.GetRequiredService<IsGatewayNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteGateway>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteGateways));

            logger.LogInformation("Deleting gateways...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteGateway(IHostApplicationBuilder builder)
    {
        ConfigureDeleteGatewayFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteGateway);
    }

    private static DeleteGateway GetDeleteGateway(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteGatewayFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteGateway))
                                       ?.AddTag("gateway.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteGatewayFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteGatewayFromApim);
    }

    private static DeleteGatewayFromApim GetDeleteGatewayFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting gateway {GatewayName}...", name);

            await GatewayUri.From(name, serviceUri)
                            .Delete(pipeline, cancellationToken);
        };
    }
}