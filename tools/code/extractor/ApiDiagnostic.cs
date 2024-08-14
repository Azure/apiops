using Azure.Core.Pipeline;
using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractApiDiagnostics(ApiName apiName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiDiagnosticName Name, ApiDiagnosticDto Dto)> ListApiDiagnostics(ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiDiagnosticArtifacts(ApiDiagnosticName name, ApiDiagnosticDto dto, ApiName apiName, CancellationToken cancellationToken);
public delegate ValueTask WriteApiDiagnosticInformationFile(ApiDiagnosticName name, ApiDiagnosticDto dto, ApiName apiName, CancellationToken cancellationToken);

internal static class ApiDiagnosticModule
{
    public static void ConfigureExtractApiDiagnostics(IHostApplicationBuilder builder)
    {
        ConfigureListApiDiagnostics(builder);
        ConfigureWriteApiDiagnosticArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractApiDiagnostics);
    }

    private static ExtractApiDiagnostics GetExtractApiDiagnostics(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApiDiagnostics>();
        var writeArtifacts = provider.GetRequiredService<WriteApiDiagnosticArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (apiName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractApiDiagnostics));

            logger.LogInformation("Extracting diagnostics for API {ApiName}...", apiName);

            await list(apiName, cancellationToken)
                    .IterParallel(async resource => await writeArtifacts(resource.Name, resource.Dto, apiName, cancellationToken),
                                  cancellationToken);
        };
    }

    private static void ConfigureListApiDiagnostics(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApiDiagnostics);
    }

    private static ListApiDiagnostics GetListApiDiagnostics(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (apiName, cancellationToken) =>
        {
            var diagnosticsUri = ApiDiagnosticsUri.From(apiName, serviceUri);
            return diagnosticsUri.List(pipeline, cancellationToken);
        };
    }

    private static void ConfigureWriteApiDiagnosticArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteApiDiagnosticInformationFile(builder);

        builder.Services.TryAddSingleton(GetWriteApiDiagnosticArtifacts);
    }

    private static WriteApiDiagnosticArtifacts GetWriteApiDiagnosticArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteApiDiagnosticInformationFile>();

        return async (name, dto, apiName, cancellationToken) =>
            await writeInformationFile(name, dto, apiName, cancellationToken);
    }

    private static void ConfigureWriteApiDiagnosticInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteApiDiagnosticInformationFile);
    }

    private static WriteApiDiagnosticInformationFile GetWriteApiDiagnosticInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, apiName, cancellationToken) =>
        {
            var informationFile = ApiDiagnosticInformationFile.From(name, apiName, serviceDirectory);

            logger.LogInformation("Writing API diagnostic information file {ApiDiagnosticInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }
}