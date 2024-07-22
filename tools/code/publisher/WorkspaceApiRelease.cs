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

public delegate ValueTask PutWorkspaceApiReleaseInApim(WorkspaceApiReleaseName name, WorkspaceApiReleaseDto dto, ApiName apiName, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask DeleteWorkspaceApiReleaseFromApim(WorkspaceApiReleaseName name, ApiName apiName, WorkspaceName workspaceName, CancellationToken cancellationToken);

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

        return async (name, dto, apiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Putting API release {WorkspaceApiReleaseName} in API {ApiName} in workspace {WorkspaceName}...", name, apiName, workspaceName);

            await WorkspaceApiReleaseUri.From(name, apiName, workspaceName, serviceUri)
                                        .PutDto(dto, pipeline, cancellationToken);
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

        return async (name, apiName, workspaceName, cancellationToken) =>
        {
            logger.LogInformation("Deleting API release {WorkspaceApiReleaseName} from API {ApiName} in workspace {WorkspaceName}...", name, apiName, workspaceName);
            logger.LogInformation("Deleting API release {WorkspaceApiReleaseName} from API {ApiName} in workspace {WorkspaceName}...", name, apiName, workspaceName);

            await WorkspaceApiReleaseUri.From(name, apiName, workspaceName, serviceUri)
                                        .Delete(pipeline, cancellationToken);
        };
    }
}