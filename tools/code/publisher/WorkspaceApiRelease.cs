using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

public delegate ValueTask PutWorkspaceApiReleaseInApim(WorkspaceApiReleaseName name, WorkspaceApiReleaseDto dto, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiReleaseFromApim(WorkspaceApiReleaseName workspaceApiReleaseName, WorkspaceApiName workspaceApiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiReleaseModule
{
    public static void ConfigurePutWorkspaceApiReleaseInApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetPutWorkspaceApiReleaseInApim);
    }

    private static PutWorkspaceApiReleaseInApim GetPutWorkspaceApiReleaseInApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiReleaseName, dto, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting release {WorkspaceApiReleaseName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiReleaseName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiReleaseUri.From(workspaceApiReleaseName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.PutDto(dto, pipeline, cancellationToken);
        };
    }

    public static void ConfigureDeleteWorkspaceApiReleaseFromApim(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteWorkspaceApiReleaseFromApim);
    }

    private static DeleteWorkspaceApiReleaseFromApim GetDeleteWorkspaceApiReleaseFromApim(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceApiReleaseName, workspaceApiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting release {WorkspaceApiReleaseName} in API {WorkspaceApiName} in workspace {WorkspaceName}...", workspaceApiReleaseName, workspaceApiName, workspaceName);

            var resourceUri = WorkspaceApiReleaseUri.From(workspaceApiReleaseName, workspaceApiName, workspaceName, serviceUri);
            await resourceUri.Delete(pipeline, cancellationToken);
        };
    }
}