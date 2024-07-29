using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

public delegate ValueTask ExtractWorkspaceApis(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiName Name, WorkspaceApiDto Dto, Option<(ApiSpecification Specification, BinaryData Contents)> SpecificationOption)> ListWorkspaceApis(WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiArtifacts(ApiName name, WorkspaceApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiInformationFile(ApiName name, WorkspaceApiDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken);
public delegate ValueTask WriteWorkspaceApiSpecificationFile(ApiName name, ApiSpecification specification, BinaryData contents, WorkspaceName workspaceName, CancellationToken cancellationToken);

internal static class WorkspaceApiModule
{
    public static void ConfigureExtractWorkspaceApis(IHostApplicationBuilder builder)
    {
        ConfigureListWorkspaceApis(builder);
        ConfigureWriteWorkspaceApiArtifacts(builder);

        builder.Services.TryAddSingleton(GetExtractWorkspaceApis);
    }

    private static ExtractWorkspaceApis GetExtractWorkspaceApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListWorkspaceApis>();
        var writeArtifacts = provider.GetRequiredService<WriteWorkspaceApiArtifacts>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (workspaceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractWorkspaceApis));

            logger.LogInformation("Extracting APIs for workspace {WorkspaceName}...", workspaceName);

            await list(workspaceName, cancellationToken)
                    // Group APIs by version set (https://github.com/Azure/apiops/issues/316).
                    // We'll process each group in parallel, but each API within a group sequentially.
                    .GroupBy(api => api.Dto.Properties.ApiVersionSetId ?? string.Empty)
                    .IterParallel(async group => await group.Iter(async api => await extractApi(api.Name, api.Dto, api.SpecificationOption, workspaceName, cancellationToken),
                                                                  cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractApi(ApiName name, WorkspaceApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            await writeArtifacts(name, dto, specificationOption, workspaceName, cancellationToken);
        }
    }

    private static void ConfigureListWorkspaceApis(IHostApplicationBuilder builder)
    {
        ApiSpecificationModule.ConfigureDefaultApiSpecification(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListWorkspaceApis);
    }

    private static ListWorkspaceApis GetListWorkspaceApis(IServiceProvider provider)
    {
        var defaultApiSpecification = provider.GetRequiredService<DefaultApiSpecification>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        return (workspaceName, cancellationToken) =>
        {
            var workspaceApisUri = WorkspaceApisUri.From(workspaceName, serviceUri);

            return workspaceApisUri.List(pipeline, cancellationToken)
                                   .SelectAwait(async api =>
                                   {
                                       var (name, dto) = api;
                                       var specificationContentsOption = await tryGetSpecificationContents(name, dto, workspaceName, cancellationToken);
                                       return (name, dto, specificationContentsOption);
                                   });
        };

        async ValueTask<Option<(ApiSpecification, BinaryData)>> tryGetSpecificationContents(ApiName name, WorkspaceApiDto dto, WorkspaceName workspaceName, CancellationToken cancellationToken)
        {
            var specificationOption = tryGetSpecification(dto);

            return await specificationOption.BindTask(async specification =>
            {
                var uri = WorkspaceApiUri.From(name, workspaceName, serviceUri);
                var contentsOption = await uri.TryGetSpecificationContents(specification, pipeline, cancellationToken);

                return from contents in contentsOption
                       select (specification, contents);
            });
        }

        Option<ApiSpecification> tryGetSpecification(WorkspaceApiDto dto) =>
            (dto.Properties.Type ?? dto.Properties.ApiType) switch
            {
                "graphql" => new ApiSpecification.GraphQl(),
                "soap" => new ApiSpecification.Wsdl(),
                "http" => defaultApiSpecification.Value,
                null => defaultApiSpecification.Value,
                _ => Option<ApiSpecification>.None
            };
    }

    private static void ConfigureWriteWorkspaceApiArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteWorkspaceApiInformationFile(builder);
        ConfigureWriteWorkspaceApiSpecificationFile(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiArtifacts);
    }

    private static WriteWorkspaceApiArtifacts GetWriteWorkspaceApiArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteWorkspaceApiInformationFile>();
        var writeSpecificationFile = provider.GetRequiredService<WriteWorkspaceApiSpecificationFile>();

        return async (name, dto, specificationContentsOption, workspaceName, cancellationToken) =>
        {
            await writeInformationFile(name, dto, workspaceName, cancellationToken);

            await specificationContentsOption.IterTask(async x =>
            {
                var (specification, contents) = x;
                await writeSpecificationFile(name, specification, contents, workspaceName, cancellationToken);
            });
        };
    }

    private static void ConfigureWriteWorkspaceApiInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiInformationFile);
    }

    private static WriteWorkspaceApiInformationFile GetWriteWorkspaceApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, workspaceName, cancellationToken) =>
        {
            var informationFile = WorkspaceApiInformationFile.From(name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace API information file {WorkspaceApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }

    private static void ConfigureWriteWorkspaceApiSpecificationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteWorkspaceApiSpecificationFile);
    }

    private static WriteWorkspaceApiSpecificationFile GetWriteWorkspaceApiSpecificationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, specification, contents, workspaceName, cancellationToken) =>
        {
            var specificationFile = WorkspaceApiSpecificationFile.From(specification, name, workspaceName, serviceDirectory);

            logger.LogInformation("Writing workspace API specification file {WorkspaceApiSpecificationFile}...", specificationFile);
            await specificationFile.WriteSpecification(contents, cancellationToken);
        };
    }
}