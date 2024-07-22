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

public delegate ValueTask PutDiagnostics(CancellationToken cancellationToken);
public delegate Option<DiagnosticName> TryParseDiagnosticName(FileInfo file);
public delegate bool IsDiagnosticNameInSourceControl(DiagnosticName name);
public delegate ValueTask PutDiagnostic(DiagnosticName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<DiagnosticDto>> FindDiagnosticDto(DiagnosticName name, CancellationToken cancellationToken);
public delegate ValueTask PutDiagnosticInApim(DiagnosticName name, DiagnosticDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteDiagnostics(CancellationToken cancellationToken);
public delegate ValueTask DeleteDiagnostic(DiagnosticName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteDiagnosticFromApim(DiagnosticName name, CancellationToken cancellationToken);

internal static class DiagnosticModule
{
    public static void ConfigurePutDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseDiagnosticName(builder);
        ConfigureIsDiagnosticNameInSourceControl(builder);
        ConfigurePutDiagnostic(builder);

        builder.Services.TryAddSingleton(GetPutDiagnostics);
    }

    private static PutDiagnostics GetPutDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsDiagnosticNameInSourceControl>();
        var put = provider.GetRequiredService<PutDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutDiagnostics));

            logger.LogInformation("Putting diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseDiagnosticName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseDiagnosticName);
    }

    private static TryParseDiagnosticName GetTryParseDiagnosticName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in DiagnosticInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsDiagnosticNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsDiagnosticNameInSourceControl);
    }

    private static IsDiagnosticNameInSourceControl GetIsDiagnosticNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(DiagnosticName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureFindDiagnosticDto(builder);
        ConfigurePutDiagnosticInApim(builder);

        builder.Services.TryAddSingleton(GetPutDiagnostic);
    }

    private static PutDiagnostic GetPutDiagnostic(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindDiagnosticDto>();
        var putInApim = provider.GetRequiredService<PutDiagnosticInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutDiagnostic))
                                       ?.AddTag("diagnostic.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindDiagnosticDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindDiagnosticDto);
    }

    private static FindDiagnosticDto GetFindDiagnosticDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<DiagnosticName, DiagnosticDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = DiagnosticInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<DiagnosticDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutDiagnosticInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutDiagnosticInApim);
    }

    private static PutDiagnosticInApim GetPutDiagnosticInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting diagnostic {DiagnosticName}...", name);

            await DiagnosticUri.From(name, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteDiagnostics(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseDiagnosticName(builder);
        ConfigureIsDiagnosticNameInSourceControl(builder);
        ConfigureDeleteDiagnostic(builder);

        builder.Services.TryAddSingleton(GetDeleteDiagnostics);
    }

    private static DeleteDiagnostics GetDeleteDiagnostics(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseDiagnosticName>();
        var isNameInSourceControl = provider.GetRequiredService<IsDiagnosticNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteDiagnostic>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteDiagnostics));

            logger.LogInformation("Deleting diagnostics...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteDiagnostic(IHostApplicationBuilder builder)
    {
        ConfigureDeleteDiagnosticFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteDiagnostic);
    }

    private static DeleteDiagnostic GetDeleteDiagnostic(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteDiagnosticFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteDiagnostic))
                                       ?.AddTag("diagnostic.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteDiagnosticFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteDiagnosticFromApim);
    }

    private static DeleteDiagnosticFromApim GetDeleteDiagnosticFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting diagnostic {DiagnosticName}...", name);

            await DiagnosticUri.From(name, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}