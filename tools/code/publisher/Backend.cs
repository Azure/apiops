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

public delegate ValueTask PutBackends(CancellationToken cancellationToken);
public delegate ValueTask DeleteBackends(CancellationToken cancellationToken);
public delegate Option<BackendName> TryParseBackendName(FileInfo file);
public delegate bool IsBackendNameInSourceControl(BackendName name);
public delegate ValueTask PutBackend(BackendName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<BackendDto>> FindBackendDto(BackendName name, CancellationToken cancellationToken);
public delegate ValueTask PutBackendInApim(BackendName name, BackendDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteBackend(BackendName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteBackendFromApim(BackendName name, CancellationToken cancellationToken);

internal static class BackendModule
{
    public static void ConfigurePutBackends(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseBackendName(builder);
        ConfigureIsBackendNameInSourceControl(builder);
        ConfigurePutBackend(builder);

        builder.Services.TryAddSingleton(GetPutBackends);
    }

    private static PutBackends GetPutBackends(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseBackendName>();
        var isNameInSourceControl = provider.GetRequiredService<IsBackendNameInSourceControl>();
        var put = provider.GetRequiredService<PutBackend>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutBackends));

            logger.LogInformation("Putting backends...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseBackendName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseBackendName);
    }

    private static TryParseBackendName GetTryParseBackendName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in BackendInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsBackendNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsBackendNameInSourceControl);
    }

    private static IsBackendNameInSourceControl GetIsBackendNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(BackendName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = BackendInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutBackend(IHostApplicationBuilder builder)
    {
        ConfigureFindBackendDto(builder);
        ConfigurePutBackendInApim(builder);

        builder.Services.TryAddSingleton(GetPutBackend);
    }

    private static PutBackend GetPutBackend(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindBackendDto>();
        var putInApim = provider.GetRequiredService<PutBackendInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutBackend))
                                       ?.AddTag("backend.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindBackendDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindBackendDto);
    }

    private static FindBackendDto GetFindBackendDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<BackendName, BackendDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = BackendInformationFile.From(name, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<BackendDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutBackendInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutBackendInApim);
    }

    private static PutBackendInApim GetPutBackendInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting backend {BackendName}...", name);

            var resourceUri = BackendUri.From(name, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteBackends(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseBackendName(builder);
        ConfigureIsBackendNameInSourceControl(builder);
        ConfigureDeleteBackend(builder);

        builder.Services.TryAddSingleton(GetDeleteBackends);
    }

    private static DeleteBackends GetDeleteBackends(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseBackendName>();
        var isNameInSourceControl = provider.GetRequiredService<IsBackendNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteBackend>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteBackends));

            logger.LogInformation("Deleting backends...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteBackend(IHostApplicationBuilder builder)
    {
        ConfigureDeleteBackendFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteBackend);
    }

    private static DeleteBackend GetDeleteBackend(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteBackendFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteBackend))
                                       ?.AddTag("backend.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteBackendFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteBackendFromApim);
    }

    private static DeleteBackendFromApim GetDeleteBackendFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting backend {BackendName}...", name);

            var resourceUri = BackendUri.From(name, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}