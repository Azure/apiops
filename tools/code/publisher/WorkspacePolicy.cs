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
public delegate Option<(WorkspacePolicyName Name, WorkspaceName WorkspaceName)> TryParseWorkspacePolicyName(FileInfo file);
public delegate bool IsWorkspacePolicyNameInSourceControl(WorkspacePolicyName name, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspacePolicy(WorkspacePolicyName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspacePolicyDto>> FindWorkspacePolicyDto(WorkspacePolicyName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspacePolicyInApim(WorkspacePolicyName name, WorkspacePolicyDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicy(WorkspacePolicyName name, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspacePolicyFromApim(WorkspacePolicyName name, WorkspaceName workspaceName, CancellationToken cancellationToken);

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
                    .Where(policy => isNameInSourceControl(policy.Name, policy.WorkspaceName))
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

        bool doesPolicyFileExist(WorkspacePolicyName name, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var policyFile = WorkspacePolicyFile.From(name, workspaceName, serviceDirectory);

            return artifactFiles.Contains(policyFile.ToFileInfo());
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

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspacePolicy))
                                       ?.AddTag("workspace_policy.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            var dtoOption = await findDto(name, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(name, dto, workspaceName, cancellationToken));
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

        return async (name, workspaceName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(name, workspaceName, cancellationToken);

            return from contents in contentsOption
                   select new WorkspacePolicyDto
                   {
                       Properties = new WorkspacePolicyDto.WorkspacePolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(WorkspacePolicyName name, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var policyFile = WorkspacePolicyFile.From(name, workspaceName, serviceDirectory);

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

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {WorkspacePolicyName} for workspace {WorkspaceName}...", name, workspaceName);

            await WorkspacePolicyUri.From(name, workspaceName, serviceUri)
                                    .PutDto(dto, pipeline, cancellationToken);
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
                    .Where(policy => isNameInSourceControl(policy.Name, policy.WorkspaceName) is false)
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

        return async (name, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspacePolicy))
                                       ?.AddTag("workspace_policy.name", name)
                                       ?.AddTag("workspace.name", workspaceName);

            await deleteFromApim(name, workspaceName, cancellationToken);
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

        return async (name, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {WorkspacePolicyName} from workspace {WorkspaceName}...", name, workspaceName);

            await WorkspacePolicyUri.From(name, workspaceName, serviceUri)
                                    .Delete(pipeline, cancellationToken);
        };
    }
}