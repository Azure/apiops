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

public delegate ValueTask PutGatewayApis(CancellationToken cancellationToken);
public delegate Option<(ApiName Name, GatewayName GatewayName)> TryParseGatewayApiName(FileInfo file);
public delegate bool IsGatewayApiNameInSourceControl(ApiName name, GatewayName gatewayName);
public delegate ValueTask PutGatewayApi(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);
public delegate ValueTask<Option<GatewayApiDto>> FindGatewayApiDto(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);
public delegate ValueTask PutGatewayApiInApim(ApiName name, GatewayApiDto dto, GatewayName gatewayName, CancellationToken cancellationToken);
public delegate ValueTask DeleteGatewayApis(CancellationToken cancellationToken);
public delegate ValueTask DeleteGatewayApi(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);
public delegate ValueTask DeleteGatewayApiFromApim(ApiName name, GatewayName gatewayName, CancellationToken cancellationToken);

internal static class GatewayApiModule
{
    public static void ConfigurePutGatewayApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryGatewayParseApiName(builder);
        ConfigureIsGatewayApiNameInSourceControl(builder);
        ConfigurePutGatewayApi(builder);

        builder.Services.TryAddSingleton(GetPutGatewayApis);
    }

    private static PutGatewayApis GetPutGatewayApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseGatewayApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsGatewayApiNameInSourceControl>();
        var put = provider.GetRequiredService<PutGatewayApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGatewayApis));

            logger.LogInformation("Putting gateway apis...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(api => isNameInSourceControl(api.Name, api.GatewayName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryGatewayParseApiName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseGatewayApiName);
    }

    private static TryParseGatewayApiName GetTryParseGatewayApiName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in GatewayApiInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsGatewayApiNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsApiNameInSourceControl);
    }

    private static IsGatewayApiNameInSourceControl GetIsApiNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(ApiName name, GatewayName gatewayName)
        {
            var artifactFiles = getArtifactFiles();
            var apiFile = GatewayApiInformationFile.From(name, gatewayName, serviceDirectory);

            return artifactFiles.Contains(apiFile.ToFileInfo());
        }
    }

    private static void ConfigurePutGatewayApi(IHostApplicationBuilder builder)
    {
        ConfigureFindGatewayApiDto(builder);
        ConfigurePutGatewayApiInApim(builder);

        builder.Services.TryAddSingleton(GetPutGatewayApi);
    }

    private static PutGatewayApi GetPutGatewayApi(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindGatewayApiDto>();
        var putInApim = provider.GetRequiredService<PutGatewayApiInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, gatewayName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutGatewayApi))
                                       ?.AddTag("gateway_api.name", name)
                                       ?.AddTag("gateway.name", gatewayName);

            var dtoOption = await findDto(name, gatewayName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, gatewayName, cancellationToken));
        };
    }

    private static void ConfigureFindGatewayApiDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindGatewayApiDto);
    }

    private static FindGatewayApiDto GetFindGatewayApiDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, gatewayName, cancellationToken) =>
        {
            var informationFile = GatewayApiInformationFile.From(name, gatewayName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<GatewayApiDto>();
        };
    }

    private static void ConfigurePutGatewayApiInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutGatewayApiInApim);
    }

    private static PutGatewayApiInApim GetPutGatewayApiInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, gatewayName, cancellationToken) =>
        {
            logger.LogInformation("Adding API {ApiName} to gateway {GatewayName}...", name, gatewayName);

            await GatewayApiUri.From(name, gatewayName, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteGatewayApis(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryGatewayParseApiName(builder);
        ConfigureIsGatewayApiNameInSourceControl(builder);
        ConfigureDeleteGatewayApi(builder);

        builder.Services.TryAddSingleton(GetDeleteGatewayApis);
    }

    private static DeleteGatewayApis GetDeleteGatewayApis(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseGatewayApiName>();
        var isNameInSourceControl = provider.GetRequiredService<IsGatewayApiNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteGatewayApi>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteGatewayApis));

            logger.LogInformation("Deleting gateway apis...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(api => isNameInSourceControl(api.Name, api.GatewayName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteGatewayApi(IHostApplicationBuilder builder)
    {
        ConfigureDeleteGatewayApiFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteGatewayApi);
    }

    private static DeleteGatewayApi GetDeleteGatewayApi(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteGatewayApiFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, gatewayName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteGatewayApi))
                                       ?.AddTag("gateway_api.name", name)
                                       ?.AddTag("gateway.name", gatewayName);

            await deleteFromApim(name, gatewayName, cancellationToken);
        };
    }

    private static void ConfigureDeleteGatewayApiFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteGatewayApiFromApim);
    }

    private static DeleteGatewayApiFromApim GetDeleteGatewayApiFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, gatewayName, cancellationToken) =>
        {
            logger.LogInformation("Removing API {ApiName} from gateway {GatewayName}...", name, gatewayName);

            await GatewayApiUri.From(name, gatewayName, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}