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

public delegate ValueTask PutApiDiagnostics(CancellationToken cancellationToken);
public delegate ValueTask DeleteApiDiagnostics(CancellationToken cancellationToken);
public delegate Option<(ApiDiagnosticName Name, ApiName ApiName)> TryParseApiDiagnosticName(FileInfo file);
public delegate bool IsApiDiagnosticNameInSourceControl(ApiDiagnosticName name, ApiName apiName);
public delegate ValueTask PutApiDiagnostic(ApiDiagnosticName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask<Option<ApiDiagnosticDto>> FindApiDiagnosticDto(ApiDiagnosticName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask PutApiDiagnosticInApim(ApiDiagnosticName name, ApiDiagnosticDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiDiagnostic(ApiDiagnosticName name, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask DeleteApiDiagnosticFromApim(ApiDiagnosticName name, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiDiagnosticModule
{
    public static void ConfigurePutApiDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiDiagnosticName(builder);
        ConfigureIsApiDiagnosticNameInSourceControl(builder);
        ConfigurePutApiDiagnostic(builder);

        builder.Services.TryAddSingleton(GetPutApiDiagnostics);
    }

    private static PutApiDiagnostics GetPutApiDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiDiagnosticNameInSourceControl>();
        var put = provider.GetRequiredService<PutApiDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiDiagnostics));

            logger.LogInformation("Putting diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.Name, resource.ApiName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseApiDiagnosticName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseApiDiagnosticName);
    }

    private static TryParseApiDiagnosticName GetTryParseApiDiagnosticName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in ApiDiagnosticInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsApiDiagnosticNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsApiDiagnosticNameInSourceControl);
    }

    private static IsApiDiagnosticNameInSourceControl GetIsApiDiagnosticNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(ApiDiagnosticName name, ApiName apiName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = ApiDiagnosticInformationFile.From(name, apiName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutApiDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureFindApiDiagnosticDto(builder);
        ConfigurePutApiDiagnosticInApim(builder);

        builder.Services.TryAddSingleton(GetPutApiDiagnostic);
    }

    private static PutApiDiagnostic GetPutApiDiagnostic(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindApiDiagnosticDto>();
        var putInApim = provider.GetRequiredService<PutApiDiagnosticInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutApiDiagnostic))
                                       ?.AddTag("api.name", apiName)
                                       ?.AddTag("api_diagnostic.name", name);

            var dtoOption = await findDto(name, apiName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, apiName, cancellationToken));
        };
    }

    private static void ConfigureFindApiDiagnosticDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        ConfigurationJsonModule.ConfigureFindConfigurationSection(builder);

        builder.Services.TryAddSingleton(GetFindApiDiagnosticDto);
    }

    public static FindApiDiagnosticDto GetFindApiDiagnosticDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var findConfigurationSection = provider.GetRequiredService<FindConfigurationSection>();

        return async (name, apiName, cancellationToken) =>
        {
            var informationFile = ApiDiagnosticInformationFile.From(name, apiName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<ApiDiagnosticDto>()
                   select overrideDto(dto, name, apiName);
        };

        ApiDiagnosticDto overrideDto(ApiDiagnosticDto dto, ApiDiagnosticName name, ApiName apiName) =>
            findConfigurationSection(["apis", apiName.Value, "diagnostics", name.Value])
                .Map(configurationJson => OverrideDtoFactory.Override(dto, configurationJson))
                .IfNone(dto);
    }

    private static void ConfigurePutApiDiagnosticInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutApiDiagnosticInApim);
    }

    private static PutApiDiagnosticInApim GetPutApiDiagnosticInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            logger.LogInformation("Adding diagnostic {ApiDiagnosticName} to API {ApiName}...", name, apiName);

            var resourceUri = ApiDiagnosticUri.From(name, apiName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteApiDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseApiDiagnosticName(builder);
        ConfigureIsApiDiagnosticNameInSourceControl(builder);
        ConfigureDeleteApiDiagnostic(builder);

        builder.Services.TryAddSingleton(GetDeleteApiDiagnostics);
    }

    private static DeleteApiDiagnostics GetDeleteApiDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseApiDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsApiDiagnosticNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteApiDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiDiagnostics));

            logger.LogInformation("Deleting diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.Name, resource.ApiName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureDeleteApiDiagnosticFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteApiDiagnostic);
    }

    private static DeleteApiDiagnostic GetDeleteApiDiagnostic(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteApiDiagnosticFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApiDiagnostic))
                                       ?.AddTag("api.name", apiName)
                                       ?.AddTag("api_diagnostic.name", name);

            await deleteFromApim(name, apiName, cancellationToken);
        };
    }

    private static void ConfigureDeleteApiDiagnosticFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApiDiagnosticFromApim);
    }

    private static DeleteApiDiagnosticFromApim GetDeleteApiDiagnosticFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, apiName, cancellationToken) =>
        {
            logger.LogInformation("Removing diagnostic {ApiDiagnosticName} from API {ApiName}...", name, apiName);

            var resourceUri = ApiDiagnosticUri.From(name, apiName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}