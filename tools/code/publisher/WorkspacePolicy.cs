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

public delegate ValueTask PutWorkspacePolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicies(CancellationToken cancellationToken);
public delegate Option<(WorkspacePolicyName WorkspacePolicyName, WorkspaceName WorkspaceName)> TryParseWorkspacePolicyName(FileInfo file);
public delegate bool IsWorkspacePolicyNameInSourceControl(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspacePolicy(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspacePolicyDto>> FindWorkspacePolicyDto(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspacePolicyInApim(WorkspacePolicyName name, WorkspacePolicyDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicy(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicyFromApim(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspacePolicyModule
{
    public static void ConfigurePutWorkspacePolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspacePolicyName(builder);
        ConfigureIsWorkspacePolicyNameInSourceControl(builder);
        ConfigurePutWorkspacePolicy(builder);

        builder.Services.TryAddSingleton(GetPutWorkspacePolicies);
    }

    private static PutWorkspacePolicies GetPutWorkspacePolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspacePolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspacePolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspacePolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspacePolicies));

            logger.LogInformation("Putting workspace policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspacePolicyName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(put.Invoke, cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspacePolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspacePolicyName);
    }

    private static TryParseWorkspacePolicyName GetTryParseWorkspacePolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in WorkspacePolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name);
    }

    private static void ConfigureIsWorkspacePolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspacePolicyNameInSourceControl);
    }

    private static IsWorkspacePolicyNameInSourceControl GetIsWorkspacePolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspacePolicyFile.From(workspacePolicyName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspacePolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspacePolicyDto(builder);
        ConfigurePutWorkspacePolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspacePolicy);
    }

    private static PutWorkspacePolicy GetPutWorkspacePolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspacePolicyDto>();
        var putInApim = provider.GetRequiredService<PutWorkspacePolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspacePolicyName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspacePolicy));

            var dtoOption = await findDto(workspacePolicyName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspacePolicyName, dto, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspacePolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspacePolicyDto);
    }

    private static FindWorkspacePolicyDto GetFindWorkspacePolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspacePolicyName, workspaceName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(workspacePolicyName, workspaceName, cancellationToken);

            return from contents in contentsOption
                   select new WorkspacePolicyDto
                   {
                       Properties = new WorkspacePolicyDto.PolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(WorkspacePolicyName workspacePolicyName, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var policyFile = WorkspacePolicyFile.From(workspacePolicyName, workspaceName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutWorkspacePolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspacePolicyInApim);
    }

    private static PutWorkspacePolicyInApim GetPutWorkspacePolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspacePolicyName, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {WorkspacePolicyName} in workspace {WorkspaceName}...", workspacePolicyName, workspaceName);

            var resourceUri = WorkspacePolicyUri.From(workspacePolicyName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspacePolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspacePolicyName(builder);
        ConfigureIsWorkspacePolicyNameInSourceControl(builder);
        ConfigureDeleteWorkspacePolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspacePolicies);
    }

    private static DeleteWorkspacePolicies GetDeleteWorkspacePolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspacePolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspacePolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspacePolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspacePolicies));

            logger.LogInformation("Deleting workspace policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspacePolicyName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(delete.Invoke, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspacePolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspacePolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspacePolicy);
    }

    private static DeleteWorkspacePolicy GetDeleteWorkspacePolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspacePolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspacePolicyName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspacePolicy));

            await deleteFromApim(workspacePolicyName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspacePolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspacePolicyFromApim);
    }

    private static DeleteWorkspacePolicyFromApim GetDeleteWorkspacePolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspacePolicyName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {WorkspacePolicyName} in workspace {WorkspaceName}...", workspacePolicyName, workspaceName);

            var resourceUri = WorkspacePolicyUri.From(workspacePolicyName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}