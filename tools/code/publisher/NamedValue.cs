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

public delegate ValueTask PutNamedValues(CancellationToken cancellationToken);
public delegate Option<NamedValueName> TryParseNamedValueName(FileInfo file);
public delegate bool IsNamedValueNameInSourceControl(NamedValueName name);
public delegate ValueTask PutNamedValue(NamedValueName name, CancellationToken cancellationToken);
public delegate ValueTask<Option<NamedValueDto>> FindNamedValueDto(NamedValueName name, CancellationToken cancellationToken);
public delegate ValueTask PutNamedValueInApim(NamedValueName name, NamedValueDto dto, CancellationToken cancellationToken);
public delegate ValueTask DeleteNamedValues(CancellationToken cancellationToken);
public delegate ValueTask DeleteNamedValue(NamedValueName name, CancellationToken cancellationToken);
public delegate ValueTask DeleteNamedValueFromApim(NamedValueName name, CancellationToken cancellationToken);

internal static class NamedValueModule
{
    public static void ConfigurePutNamedValues(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseNamedValueName(builder);
        ConfigureIsNamedValueNameInSourceControl(builder);
        ConfigurePutNamedValue(builder);

        builder.Services.TryAddSingleton(GetPutNamedValues);
    }

    private static PutNamedValues GetPutNamedValues(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseNamedValueName>();
        var isNameInSourceControl = provider.GetRequiredService<IsNamedValueNameInSourceControl>();
        var put = provider.GetRequiredService<PutNamedValue>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutNamedValues));

            logger.LogInformation("Putting named values...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(isNameInSourceControl.Invoke)
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseNamedValueName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseNamedValueName);
    }

    private static TryParseNamedValueName GetTryParseNamedValueName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in NamedValueInformationFile.TryParse(file, serviceDirectory)
                       select informationFile.Parent.Name;
    }

    private static void ConfigureIsNamedValueNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsNamedValueNameInSourceControl);
    }

    private static IsNamedValueNameInSourceControl GetIsNamedValueNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(NamedValueName name)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = NamedValueInformationFile.From(name, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutNamedValue(IHostApplicationBuilder builder)
    {
        ConfigureFindNamedValueDto(builder);
        ConfigurePutNamedValueInApim(builder);

        builder.Services.TryAddSingleton(GetPutNamedValue);
    }

    private static PutNamedValue GetPutNamedValue(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindNamedValueDto>();
        var putInApim = provider.GetRequiredService<PutNamedValueInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutNamedValue))
                                       ?.AddTag("named_value.name", name);

            var dtoOption = await findDto(name, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, cancellationToken));
        };
    }

    private static void ConfigureFindNamedValueDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);
        OverrideDtoModule.ConfigureOverrideDtoFactory(builder);

        builder.Services.TryAddSingleton(GetFindNamedValueDto);
    }

    private static FindNamedValueDto GetFindNamedValueDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();
        var overrideFactory = provider.GetRequiredService<OverrideDtoFactory>();

        var overrideDto = overrideFactory.Create<NamedValueName, NamedValueDto>();

        return async (name, cancellationToken) =>
        {
            var informationFile = NamedValueInformationFile.From(name, serviceDirectory);
            var informationFileInfo = informationFile.ToFileInfo();

            var contentsOption = await tryGetFileContents(informationFileInfo, cancellationToken);

            return from contents in contentsOption
                   let dto = contents.ToObjectFromJson<NamedValueDto>()
                   select overrideDto(name, dto);
        };
    }

    private static void ConfigurePutNamedValueInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutNamedValueInApim);
    }

    private static PutNamedValueInApim GetPutNamedValueInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            // Don't put secret named values without a value or keyvault identifier
            if (dto.Properties.Secret is true && dto.Properties.Value is null && dto.Properties.KeyVault?.SecretIdentifier is null)
            {
                logger.LogWarning("Named value {NamedValueName} is secret, but no value or keyvault identifier was specified. Skipping it...", name);
                return;
            }

            logger.LogInformation("Putting named value {NamedValueName}...", name);

            await NamedValueUri.From(name, serviceUri)
                               .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteNamedValues(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseNamedValueName(builder);
        ConfigureIsNamedValueNameInSourceControl(builder);
        ConfigureDeleteNamedValue(builder);

        builder.Services.TryAddSingleton(GetDeleteNamedValues);
    }

    private static DeleteNamedValues GetDeleteNamedValues(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseNamedValueName>();
        var isNameInSourceControl = provider.GetRequiredService<IsNamedValueNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteNamedValue>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteNamedValues));

            logger.LogInformation("Deleting named values...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(name => isNameInSourceControl(name) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteNamedValue(IHostApplicationBuilder builder)
    {
        ConfigureDeleteNamedValueFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteNamedValue);
    }

    private static DeleteNamedValue GetDeleteNamedValue(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteNamedValueFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteNamedValue))
                                       ?.AddTag("named_value.name", name);

            await deleteFromApim(name, cancellationToken);
        };
    }

    private static void ConfigureDeleteNamedValueFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteNamedValueFromApim);
    }

    private static DeleteNamedValueFromApim GetDeleteNamedValueFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, cancellationToken) =>
        {
            logger.LogInformation("Deleting named value {NamedValueName}...", name);

            await NamedValueUri.From(name, serviceUri)
                               .Delete(pipeline, cancellationToken);
        };
    }
}