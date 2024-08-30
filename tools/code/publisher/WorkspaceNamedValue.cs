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
public delegate ValueTask DeleteWorkspaceNamedValues(CancellationToken cancellationToken);
public delegate Option<(WorkspaceNamedValueName WorkspaceNamedValueName, WorkspaceName WorkspaceName)> TryParseWorkspaceNamedValueName(FileInfo file);
public delegate bool IsWorkspaceNamedValueNameInSourceControl(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceNamedValue(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceNamedValueDto>> FindWorkspaceNamedValueDto(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceNamedValueInApim(WorkspaceNamedValueName name, WorkspaceNamedValueDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceNamedValue(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceNamedValueFromApim(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceNamedValueModule
{
    public static void ConfigurePutWorkspaceNamedValues(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceNamedValueName(builder);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceNamedValueName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceNamedValueName(IHostApplicationBuilder builder)
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

        builder.Services.TryAddSingleton(GetIsWorkspaceNamedValueNameInSourceControl);
    }

    private static IsWorkspaceNamedValueNameInSourceControl GetIsWorkspaceNamedValueNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesInformationFileExist;

        bool doesInformationFileExist(WorkspaceNamedValueName workspaceNamedValueName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceNamedValueInformationFile.From(workspaceNamedValueName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
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

        return async (workspaceNamedValueName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceNamedValue));

            var dtoOption = await findDto(workspaceNamedValueName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceNamedValueName, dto, workspaceName, cancellationToken));
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

        return async (workspaceNamedValueName, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceNamedValueInformationFile.From(workspaceNamedValueName, workspaceName, serviceDirectory);
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

        return async (workspaceNamedValueName, dto, workspaceName, cancellationToken) =>
        {
            // Don't put secret named values without a value or keyvault identifier
            if (dto.Properties.Secret is true && dto.Properties.Value is null && dto.Properties.KeyVault?.SecretIdentifier is null)
            {
                logger.LogWarning("Named value {WorkspaceNamedValueName} in workspace {WorkspaceName} is secret, but no value or keyvault identifier was specified. Skipping it...", workspaceNamedValueName, workspaceName);
                return;
            }

            logger.LogInformation("Putting named value {WorkspaceNamedValueName} in workspace {WorkspaceName}...", workspaceNamedValueName, workspaceName);

            var resourceUri = WorkspaceNamedValueUri.From(workspaceNamedValueName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceNamedValues(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceNamedValueName(builder);
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
                    .Where(resource => isNameInSourceControl(resource.WorkspaceNamedValueName, resource.WorkspaceName) is false)
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

        return async (workspaceNamedValueName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceNamedValue));

            await deleteFromApim(workspaceNamedValueName, workspaceName, cancellationToken);
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

        return async (workspaceNamedValueName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting named value {WorkspaceNamedValueName} in workspace {WorkspaceName}...", workspaceNamedValueName, workspaceName);

            var resourceUri = WorkspaceNamedValueUri.From(workspaceNamedValueName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}