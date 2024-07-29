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

public delegate ValueTask PutLoggers(CancellationToken cancellationToken);
public delegate Option<LoggerName> TryParseLoggerName(FileInfo file);
public delegate bool IsLoggerNameInSourceControl(LoggerName name);
public delegate ValueTask PutLogger(LoggerName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<LoggerDto>> FindLoggerDto(LoggerName name, CancellationToken cancellationToken);
public delegate ValueTask PutLoggerInApim(LoggerName name, LoggerDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteLoggers(CancellationToken cancellationToken);
public delegate ValueTask DeleteLogger(LoggerName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteLoggerFromApim(LoggerName name, CancellationToken cancellationToken);

internal static class LoggerModule
{
    public static void ConfigurePutLoggers(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseLoggerName(builder);
        ConfigureIsLoggerNameInSourceControl(builder);
        ConfigurePutLogger(builder);

        builder.Services.TryAddSingleton(GetPutLoggers);
    }

    private static PutLoggers GetPutLoggers(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseLoggerName>();
        var isNameInSourceControl = provider.GetRequiredService<IsLoggerNameInSourceControl>();
        var put = provider.GetRequiredService<PutLogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutLoggers));

            logger.LogInformation("Putting loggers...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseLoggerName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseLoggerName);
    }

    private static TryParseLoggerName GetTryParseLoggerName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in LoggerInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsLoggerNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsLoggerNameInSourceControl);
    }

    private static IsLoggerNameInSourceControl GetIsLoggerNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(LoggerName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = LoggerInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutLogger(IHostApplicationBuilder builder)
    {
        ConfigureFindLoggerDto(builder);
        ConfigurePutLoggerInApim(builder);

        builder.Services.TryAddSingleton(GetPutLogger);
    }

    private static PutLogger GetPutLogger(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindLoggerDto>();
        var putInApim = provider.GetRequiredService<PutLoggerInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutLogger))
                                       ?.AddTag("logger.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindLoggerDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindLoggerDto);
    }

    private static FindLoggerDto GetFindLoggerDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<LoggerName, LoggerDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = LoggerInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<LoggerDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutLoggerInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutLoggerInApim);
    }

    private static PutLoggerInApim GetPutLoggerInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            logger.LogInformation("Putting logger {LoggerName}...", name);

            await LoggerUri.From(name, serviceUri)
                           .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteLoggers(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseLoggerName(builder);
        ConfigureIsLoggerNameInSourceControl(builder);
        ConfigureDeleteLogger(builder);

        builder.Services.TryAddSingleton(GetDeleteLoggers);
    }

    private static DeleteLoggers GetDeleteLoggers(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseLoggerName>();
        var isNameInSourceControl = provider.GetRequiredService<IsLoggerNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteLogger>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteLoggers));

            logger.LogInformation("Deleting loggers...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteLogger(IHostApplicationBuilder builder)
    {
        ConfigureDeleteLoggerFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteLogger);
    }

    private static DeleteLogger GetDeleteLogger(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteLoggerFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteLogger))
                                       ?.AddTag("logger.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteLoggerFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteLoggerFromApim);
    }

    private static DeleteLoggerFromApim GetDeleteLoggerFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting logger {LoggerName}...", name);

            await LoggerUri.From(name, serviceUri)
                           .Delete(pipeline, cancellationToken);
        };
    }
}