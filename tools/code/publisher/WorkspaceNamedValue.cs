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

public delegate ValueTask PutWorkspaceNamedValues(CancellationToken cancellationToken);
public delegate Option<(NamedValueName Name, WorkspaceName WorkspaceName)> TryParseWorkspaceNamedValueName(FileInfo file);
public delegate bool IsWorkspaceNamedValueNameInSourceControl(NamedValueName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceNamedValue(NamedValueName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceNamedValueDto>> FindWorkspaceNamedValueDto(NamedValueName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceNamedValueInApim(NamedValueName name, WorkspaceNamedValueDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceNamedValues(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceNamedValue(NamedValueName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceNamedValueFromApim(NamedValueName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceNamedValueModule
{
    public static void ConfigurePutWorkspaceNamedValues(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseNamedValueName(builder);
        ConfigureIsWorkspaceNamedValueNameInSourceControl(builder);
        ConfigurePutWorkspaceNamedValue(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceNamedValues);
    }

    private static PutWorkspaceNamedValues GetPutWorkspaceNamedValues(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceNamedValueName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceNamedValueNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceNamedValue>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceNamedValues));

            logger.LogInformation("Putting workspace named values...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryWorkspaceParseNamedValueName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceNamedValueName);
    }

    private static TryParseWorkspaceNamedValueName GetTryParseWorkspaceNamedValueName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from informationFile in WorkspaceNamedValueInformationFile.TryParse(file, serviceDirectory)
                       select (informationFile.Parent.Name, informationFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceNamedValueNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsNamedValueNameInSourceControl);
    }

    private static IsWorkspaceNamedValueNameInSourceControl GetIsNamedValueNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(NamedValueName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var tagFile = WorkspaceNamedValueInformationFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(tagFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceNamedValue(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceNamedValueDto(builder);
        ConfigurePutWorkspaceNamedValueInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceNamedValue);
    }

    private static PutWorkspaceNamedValue GetPutWorkspaceNamedValue(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceNamedValueDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceNamedValueInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceNamedValue))
                                       ?.AddTag("workspace_named_value.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceNamedValueDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceNamedValueDto);
    }

    private static FindWorkspaceNamedValueDto GetFindWorkspaceNamedValueDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (name, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceNamedValueInformationFile.From(name, workspaceName, serviceDirectory);
            var contentsOption = await tryGetFileContents(informationFile.ToFileInfo(), cancellationToken);

            return from contents in contentsOption
                   select contents.ToObjectFromJson<WorkspaceNamedValueDto>();
        };
    }

    private static void ConfigurePutWorkspaceNamedValueInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceNamedValueInApim);
    }

    private static PutWorkspaceNamedValueInApim GetPutWorkspaceNamedValueInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            // Don't put secret named values without a value or keyvault identifier
            if (dto.Properties.Secret is true && dto.Properties.Value is null && dto.Properties.KeyVault?.SecretIdentifier is null)
            {
                logger.LogWarning("Named value {NamedValueName} in workspace {WorkspaceName} is secret, but no value or keyvault identifier was specified. Skipping it...", name, workspaceName);
                return;
            }

            logger.LogInformation("Adding named value {NamedValueName} to workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceNamedValueUri.From(name, workspaceName, serviceUri)
                                        .PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceNamedValues(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryWorkspaceParseNamedValueName(builder);
        ConfigureIsWorkspaceNamedValueNameInSourceControl(builder);
        ConfigureDeleteWorkspaceNamedValue(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceNamedValues);
    }

    private static DeleteWorkspaceNamedValues GetDeleteWorkspaceNamedValues(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceNamedValueName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceNamedValueNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceNamedValue>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceNamedValues));

            logger.LogInformation("Deleting workspace named values...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(tag => isNameInSourceControl(tag.Name, tag.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceNamedValue(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceNamedValueFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceNamedValue);
    }

    private static DeleteWorkspaceNamedValue GetDeleteWorkspaceNamedValue(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceNamedValueFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceNamedValue))
                                       ?.AddTag("workspace_named_value.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceNamedValueFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceNamedValueFromApim);
    }

    private static DeleteWorkspaceNamedValueFromApim GetDeleteWorkspaceNamedValueFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Removing named value {NamedValueName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspaceNamedValueUri.From(name, workspaceName, serviceUri)
                                        .Delete(pipeline, cancellationToken);
        };
    }
}