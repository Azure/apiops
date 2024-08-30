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

public delegate ValueTask PutWorkspaceProductPolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductPolicies(CancellationToken cancellationToken);
public delegate Option<(WorkspaceProductPolicyName WorkspaceProductPolicyName, WorkspaceProductName WorkspaceProductName, WorkspaceName WorkspaceName)> TryParseWorkspaceProductPolicyName(FileInfo file);
public delegate bool IsWorkspaceProductPolicyNameInSourceControl(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceProductPolicy(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceProductPolicyDto>> FindWorkspaceProductPolicyDto(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceProductPolicyInApim(WorkspaceProductPolicyName name, WorkspaceProductPolicyDto dto, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductPolicy(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceProductPolicyFromApim(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceProductPolicyModule
{
    public static void ConfigurePutWorkspaceProductPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductPolicyName(builder);
        ConfigureIsWorkspaceProductPolicyNameInSourceControl(builder);
        ConfigurePutWorkspaceProductPolicy(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductPolicies);
    }

    private static PutWorkspaceProductPolicies GetPutWorkspaceProductPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductPolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceProductPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProductPolicies));

            logger.LogInformation("Putting workspace product policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceProductPolicyName, resource.WorkspaceProductName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceProductPolicyName, resource.WorkspaceProductName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceProductPolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceProductPolicyName);
    }

    private static TryParseWorkspaceProductPolicyName GetTryParseWorkspaceProductPolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in WorkspaceProductPolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name, policyFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceProductPolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceProductPolicyNameInSourceControl);
    }

    private static IsWorkspaceProductPolicyNameInSourceControl GetIsWorkspaceProductPolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceProductPolicyFile.From(workspaceProductPolicyName, workspaceProductName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceProductPolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceProductPolicyDto(builder);
        ConfigurePutWorkspaceProductPolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductPolicy);
    }

    private static PutWorkspaceProductPolicy GetPutWorkspaceProductPolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceProductPolicyDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceProductPolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceProductPolicy));

            var dtoOption = await findDto(workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceProductPolicyName, dto, workspaceProductName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceProductPolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceProductPolicyDto);
    }

    private static FindWorkspaceProductPolicyDto GetFindWorkspaceProductPolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken);

            return from contents in contentsOption
                   select new WorkspaceProductPolicyDto
                   {
                       Properties = new WorkspaceProductPolicyDto.PolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(WorkspaceProductPolicyName workspaceProductPolicyName, WorkspaceProductName workspaceProductName, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var policyFile = WorkspaceProductPolicyFile.From(workspaceProductPolicyName, workspaceProductName, workspaceName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutWorkspaceProductPolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceProductPolicyInApim);
    }

    private static PutWorkspaceProductPolicyInApim GetPutWorkspaceProductPolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductPolicyName, dto, workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {WorkspaceProductPolicyName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductPolicyName, workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductPolicyUri.From(workspaceProductPolicyName, workspaceProductName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceProductPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceProductPolicyName(builder);
        ConfigureIsWorkspaceProductPolicyNameInSourceControl(builder);
        ConfigureDeleteWorkspaceProductPolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductPolicies);
    }

    private static DeleteWorkspaceProductPolicies GetDeleteWorkspaceProductPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceProductPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceProductPolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceProductPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProductPolicies));

            logger.LogInformation("Deleting workspace product policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceProductPolicyName, resource.WorkspaceProductName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceProductPolicyName, resource.WorkspaceProductName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductPolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceProductPolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductPolicy);
    }

    private static DeleteWorkspaceProductPolicy GetDeleteWorkspaceProductPolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceProductPolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceProductPolicy));

            await deleteFromApim(workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceProductPolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceProductPolicyFromApim);
    }

    private static DeleteWorkspaceProductPolicyFromApim GetDeleteWorkspaceProductPolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceProductPolicyName, workspaceProductName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {WorkspaceProductPolicyName} in product {WorkspaceProductName} in workspace {WorkspaceName}...", workspaceProductPolicyName, workspaceProductName, workspaceName);

            var resourceUri = WorkspaceProductPolicyUri.From(workspaceProductPolicyName, workspaceProductName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}