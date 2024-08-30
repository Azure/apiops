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

public delegate ValueTask PutWorkspaceApiOperationPolicies(CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiOperationPolicies(CancellationToken cancellationToken);
public delegate Option<(WorkspaceApiOperationPolicyName WorkspaceApiOperationPolicyName, WorkspaceApiOperationName WorkspaceApiOperationName, WorkspaceApiName WorkspaceApiName, WorkspaceName WorkspaceName)> TryParseWorkspaceApiOperationPolicyName(FileInfo file);
public delegate bool IsWorkspaceApiOperationPolicyNameInSourceControl(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName);
public delegate ValueTask PutWorkspaceApiOperationPolicy(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask<Option<WorkspaceApiOperationPolicyDto>> FindWorkspaceApiOperationPolicyDto(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask PutWorkspaceApiOperationPolicyInApim(WorkspaceApiOperationPolicyName name, WorkspaceApiOperationPolicyDto dto, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiOperationPolicy(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiOperationPolicyFromApim(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiOperationPolicyModule
{
    public static void ConfigurePutWorkspaceApiOperationPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiOperationPolicyName(builder);
        ConfigureIsWorkspaceApiOperationPolicyNameInSourceControl(builder);
        ConfigurePutWorkspaceApiOperationPolicy(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiOperationPolicies);
    }

    private static PutWorkspaceApiOperationPolicies GetPutWorkspaceApiOperationPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiOperationPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiOperationPolicyNameInSourceControl>();
        var put = provider.GetRequiredService<PutWorkspaceApiOperationPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApiOperationPolicies));

            logger.LogInformation("Putting workspace API operation policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiOperationPolicyName, resource.WorkspaceApiOperationName, resource.WorkspaceApiName, resource.WorkspaceName))
                    .Distinct()
                    .IterParallel(resource => put(resource.WorkspaceApiOperationPolicyName, resource.WorkspaceApiOperationName, resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureTryParseWorkspaceApiOperationPolicyName(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetTryParseWorkspaceApiOperationPolicyName);
    }

    private static TryParseWorkspaceApiOperationPolicyName GetTryParseWorkspaceApiOperationPolicyName(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return file => from policyFile in WorkspaceApiOperationPolicyFile.TryParse(file, serviceDirectory)
                       select (policyFile.Name, policyFile.Parent.Name, policyFile.Parent.Parent.Parent.Name, policyFile.Parent.Parent.Parent.Parent.Parent.Name);
    }

    private static void ConfigureIsWorkspaceApiOperationPolicyNameInSourceControl(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetArtifactFiles(builder);
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetIsWorkspaceApiOperationPolicyNameInSourceControl);
    }

    private static IsWorkspaceApiOperationPolicyNameInSourceControl GetIsWorkspaceApiOperationPolicyNameInSourceControl(IServiceProvider provider)
    {
        var getArtifactFiles = provider.GetRequiredService<GetArtifactFiles>();
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();

        return doesPolicyFileExist;

        bool doesPolicyFileExist(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName)
        {
            var artifactFiles = getArtifactFiles();
            var informationFile = WorkspaceApiOperationPolicyFile.From(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, serviceDirectory);

            return artifactFiles.Contains(informationFile.ToFileInfo());
        }
    }

    private static void ConfigurePutWorkspaceApiOperationPolicy(IHostApplicationBuilder builder)
    {
        ConfigureFindWorkspaceApiOperationPolicyDto(builder);
        ConfigurePutWorkspaceApiOperationPolicyInApim(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiOperationPolicy);
    }

    private static PutWorkspaceApiOperationPolicy GetPutWorkspaceApiOperationPolicy(IServiceProvider provider)
    {
        var findDto = provider.GetRequiredService<FindWorkspaceApiOperationPolicyDto>();
        var putInApim = provider.GetRequiredService<PutWorkspaceApiOperationPolicyInApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(PutWorkspaceApiOperationPolicy));

            var dtoOption = await findDto(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken);
            await dtoOption.IterTask(async dto => await putInApim(workspaceApiOperationPolicyName, dto, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken));
        };
    }

    private static void ConfigureFindWorkspaceApiOperationPolicyDto(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);
        CommonModule.ConfigureTryGetFileContents(builder);

        builder.Services.TryAddSingleton(GetFindWorkspaceApiOperationPolicyDto);
    }

    private static FindWorkspaceApiOperationPolicyDto GetFindWorkspaceApiOperationPolicyDto(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var tryGetFileContents = provider.GetRequiredService<TryGetFileContents>();

        return async (workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            var contentsOption = await tryGetPolicyContents(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken);

            return from contents in contentsOption
                   select new WorkspaceApiOperationPolicyDto
                   {
                       Properties = new WorkspaceApiOperationPolicyDto.PolicyContract
                       {
                           Format = "rawxml",
                           Value = contents.ToString()
                       }
                   };
        };

        async ValueTask<Option<BinaryData>> tryGetPolicyContents(WorkspaceApiOperationPolicyName workspaceApiOperationPolicyName, WorkspaceApiOperationName workspaceApiOperationName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var policyFile = WorkspaceApiOperationPolicyFile.From(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, serviceDirectory);

            return await tryGetFileContents(policyFile.ToFileInfo(), cancellationToken);
        }
    }

    private static void ConfigurePutWorkspaceApiOperationPolicyInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiOperationPolicyInApim);
    }

    private static PutWorkspaceApiOperationPolicyInApim GetPutWorkspaceApiOperationPolicyInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiOperationPolicyName, dto, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting policy {WorkspaceApiOperationPolicyName} in operation {WorkspaceApiOperationName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiOperationPolicyUri.From(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceApiOperationPolicies(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetPublisherFiles(builder);
        ConfigureTryParseWorkspaceApiOperationPolicyName(builder);
        ConfigureIsWorkspaceApiOperationPolicyNameInSourceControl(builder);
        ConfigureDeleteWorkspaceApiOperationPolicy(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiOperationPolicies);
    }

    private static DeleteWorkspaceApiOperationPolicies GetDeleteWorkspaceApiOperationPolicies(IServiceProvider provider)
    {
        var getPublisherFiles = provider.GetRequiredService<GetPublisherFiles>();
        var tryParseName = provider.GetRequiredService<TryParseWorkspaceApiOperationPolicyName>();
        var isNameInSourceControl = provider.GetRequiredService<IsWorkspaceApiOperationPolicyNameInSourceControl>();
        var delete = provider.GetRequiredService<DeleteWorkspaceApiOperationPolicy>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApiOperationPolicies));

            logger.LogInformation("Deleting workspace API operation policies...");

            await getPublisherFiles()
                    .Choose(tryParseName.Invoke)
                    .Where(resource => isNameInSourceControl(resource.WorkspaceApiOperationPolicyName, resource.WorkspaceApiOperationName, resource.WorkspaceApiName, resource.WorkspaceName) is false)
                    .Distinct()
                    .IterParallel(resource => delete(resource.WorkspaceApiOperationPolicyName, resource.WorkspaceApiOperationName, resource.WorkspaceApiName, resource.WorkspaceName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApiOperationPolicy(IHostApplicationBuilder builder)
    {
        ConfigureDeleteWorkspaceApiOperationPolicyFromApim(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiOperationPolicy);
    }

    private static DeleteWorkspaceApiOperationPolicy GetDeleteWorkspaceApiOperationPolicy(IServiceProvider provider)
    {
        var deleteFromApim = provider.GetRequiredService<DeleteWorkspaceApiOperationPolicyFromApim>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteWorkspaceApiOperationPolicy));

            await deleteFromApim(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken);
        };
    }

    private static void ConfigureDeleteWorkspaceApiOperationPolicyFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiOperationPolicyFromApim);
    }

    private static DeleteWorkspaceApiOperationPolicyFromApim GetDeleteWorkspaceApiOperationPolicyFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting policy {WorkspaceApiOperationPolicyName} in operation {WorkspaceApiOperationName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiOperationPolicyUri.From(workspaceApiOperationPolicyName, workspaceApiOperationName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}