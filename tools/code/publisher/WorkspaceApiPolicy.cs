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

public delegate ValueTask PutWorkspaceApiPolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiPolicies(CancellationToken cancellationToken);
public delegate Option<(WorkspaceApiPolicyName WorkspaceApiPolicyName, WorkspaceApiName WorkspaceApiName, WorkspaceName WorkspaceName)> TryParseWorkspaceApiPolicyName(FileInfo file);
public delegate bool IsWorkspaceApiPolicyNameInSourceControl(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceApiPolicy(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceApiPolicyDto>> FindWorkspaceApiPolicyDto(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceApiPolicyInApim(WorkspaceApiPolicyName name, WorkspaceApiPolicyDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiPolicy(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiPolicyFromApim(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiPolicyModule
{
    public static void ConfigurePutWorkspaceApiPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiPolicyName(builder);
        ConfigureIsWorkspaceApiPolicyNameInSourceControl(builder);
        ConfigurePutWorkspaceApiPolicy(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiPolicies);
    }

    private static PutWorkspaceApiPolicies GetPutWorkspaceApiPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiPolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceApiPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApiPolicies));

            logger.LogInformation("Putting workspace API policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiPolicyName, resource.WorkspaceApiName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceApiPolicyName, resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceApiPolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceApiPolicyName);
    }

    private static TryParseWorkspaceApiPolicyName GetTryParseWorkspaceApiPolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in WorkspaceApiPolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name, policyFile.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceApiPolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceApiPolicyNameInSourceControl);
    }

    private static IsWorkspaceApiPolicyNameInSourceControl GetIsWorkspaceApiPolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceApiPolicyFile.From(workspaceApiPolicyName, workspaceApiName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceApiPolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiPolicyDto(builder);
        ConfigurePutWorkspaceApiPolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiPolicy);
    }

    private static PutWorkspaceApiPolicy GetPutWorkspaceApiPolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiPolicyDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceApiPolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApiPolicy));

            var dtoOption = await findDto(workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceApiPolicyName, dto, workspaceApiName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceApiPolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiPolicyDto);
    }

    private static FindWorkspaceApiPolicyDto GetFindWorkspaceApiPolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken);

            return from contents in contentsOption
                   select new WorkspaceApiPolicyDto
                   {
                       Properties = new WorkspaceApiPolicyDto.PolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(WorkspaceApiPolicyName workspaceApiPolicyName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var policyFile = WorkspaceApiPolicyFile.From(workspaceApiPolicyName, workspaceApiName, workspaceName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutWorkspaceApiPolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiPolicyInApim);
    }

    private static PutWorkspaceApiPolicyInApim GetPutWorkspaceApiPolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiPolicyName, dto, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {WorkspaceApiPolicyName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiPolicyName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiPolicyUri.From(workspaceApiPolicyName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceApiPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiPolicyName(builder);
        ConfigureIsWorkspaceApiPolicyNameInSourceControl(builder);
        ConfigureDeleteWorkspaceApiPolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiPolicies);
    }

    private static DeleteWorkspaceApiPolicies GetDeleteWorkspaceApiPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiPolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceApiPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApiPolicies));

            logger.LogInformation("Deleting workspace API policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiPolicyName, resource.WorkspaceApiName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceApiPolicyName, resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApiPolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceApiPolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiPolicy);
    }

    private static DeleteWorkspaceApiPolicy GetDeleteWorkspaceApiPolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceApiPolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApiPolicy));

            await deleteFromApim(workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApiPolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiPolicyFromApim);
    }

    private static DeleteWorkspaceApiPolicyFromApim GetDeleteWorkspaceApiPolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiPolicyName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {WorkspaceApiPolicyName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiPolicyName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiPolicyUri.From(workspaceApiPolicyName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}