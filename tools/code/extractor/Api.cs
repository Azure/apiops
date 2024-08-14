using Azure.Core.Pipeline;
using common;
using Flurl;
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

public delegate ValueTask ExtractApis(CancellationToken cancellationToken);
public delegate IAsyncEnumerable<(ApiName Name, ApiDto Dto, Option<(ApiSpecification Specification, BinaryData Contents)> SpecificationOption)> ListApis(CancellationToken cancellationToken);
public delegate ValueTask WriteApiArtifacts(ApiName name, ApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, CancellationToken cancellationToken);
public delegate ValueTask WriteApiInformationFile(ApiName name, ApiDto dto, CancellationToken cancellationToken);
public delegate ValueTask WriteApiSpecificationFile(ApiName name, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken);

internal static class ApiModule
{
    public static void ConfigureExtractApis(IHostApplicationBuilder builder)
    {
        ConfigureListApis(builder);
        ConfigureWriteApiArtifacts(builder);
        ApiPolicyModule.ConfigureExtractApiPolicies(builder);
        ApiTagModule.ConfigureExtractApiTags(builder);
        ApiDiagnosticModule.ConfigureExtractApiDiagnostics(builder);
        ApiOperationModule.ConfigureExtractApiOperations(builder);

        builder.Services.TryAddSingleton(GetExtractApis);
    }

    private static ExtractApis GetExtractApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApis>();
        var writeArtifacts = provider.GetRequiredService<WriteApiArtifacts>();
        var extractApiPolicies = provider.GetRequiredService<ExtractApiPolicies>();
        var extractApiTags = provider.GetRequiredService<ExtractApiTags>();
        var extractApiDiagnostics = provider.GetRequiredService<ExtractApiDiagnostics>();
        var extractApiOperations = provider.GetRequiredService<ExtractApiOperations>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ExtractApis));

            logger.LogInformation("Extracting APIs...");

            await list(cancellationToken)
                    // Group APIs by version set (https://github.com/Azure/apiops/issues/316).
                    // We'll process each group in parallel, but each API within a group sequentially.
                    .GroupBy(api => api.Dto.Properties.ApiVersionSetId ?? Guid.NewGuid().ToString())
                    .IterParallel(async group => await group.Iter(async api => await extractApi(api.Name, api.Dto, api.SpecificationOption, cancellationToken),
                                                                  cancellationToken),
                                  cancellationToken);
        };

        async ValueTask extractApi(ApiName name, ApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, CancellationToken cancellationToken)
        {
            await writeArtifacts(name, dto, specificationOption, cancellationToken);
            await extractApiPolicies(name, cancellationToken);
            await extractApiTags(name, cancellationToken);
            await extractApiDiagnostics(name, cancellationToken);
            await extractApiOperations(name, cancellationToken);
        }
    }

    private static void ConfigureListApis(IHostApplicationBuilder builder)
    {
        ConfigurationModule.ConfigureFindConfigurationNamesFactory(builder);
        ApiSpecificationModule.ConfigureDefaultApiSpecification(builder);
        AzureModule.ConfigureManagementServiceUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApis);
    }

    private static ListApis GetListApis(IServiceProvider provider)
    {
        var findConfigurationNamesFactory = provider.GetRequiredService<FindConfigurationNamesFactory>();
        var defaultApiSpecification = provider.GetRequiredService<DefaultApiSpecification>();
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();

        var findConfigurationApis = findConfigurationNamesFactory.Create<ApiName>();
        var findConfigurationVersionSets = findConfigurationNamesFactory.Create<VersionSetName>();

        return cancellationToken =>
            list(cancellationToken)
                .Where(api => shouldExtractApiDto(api.Dto))
                .SelectAwait(async api =>
                {
                    var (name, dto) = api;
                    var specificationContentsOption = await tryGetSpecificationContents(name, dto, cancellationToken);
                    return (name, dto, specificationContentsOption);
                });

        IAsyncEnumerable<(ApiName Name, ApiDto Dto)> list(CancellationToken cancellationToken) =>
            findConfigurationApis()
                .Map(names => listFromSet(names, cancellationToken))
                .IfNone(() => listAll(cancellationToken));

        IAsyncEnumerable<(ApiName, ApiDto)> listFromSet(IEnumerable<ApiName> names, CancellationToken cancellationToken) =>
            names.ToAsyncEnumerable()
                 // Ensure API exists
                 .WhereAwait(async name =>
                 {
                     var uri = ApiUri.From(name, serviceUri);
                     var dtoOption = await uri.TryGetDto(pipeline, cancellationToken);
                     return dtoOption.IsSome;
                 })
                 // Get all API revisions
                 .SelectMany(name => listAllRevisions(name, cancellationToken));

        IAsyncEnumerable<(ApiName, ApiDto)> listAllRevisions(ApiName name, CancellationToken cancellationToken)
        {
            var rootName = ApiName.GetRootName(name);
            var rootNameUri = ApiUri.From(name, serviceUri);
            var revisionsUri = rootNameUri.ToUri().AppendPathSegment("revisions").ToUri();

            return pipeline.ListJsonObjects(revisionsUri, cancellationToken)
                           // Get name for each revision. If the revision is current, use the root name.
                           .Select(jsonObject =>
                           {
                               var revisionNumberInt = jsonObject.GetIntProperty("apiRevision");
                               var revisionNumber = ApiRevisionNumber.From(revisionNumberInt);

                               var isCurrent = jsonObject.GetBoolProperty("isCurrent");

                               return isCurrent ? name : ApiName.GetRevisionedName(rootName, revisionNumber);
                           })
                           // Get DTO for each revision
                           .SelectAwait(async name =>
                           {
                               var uri = ApiUri.From(name, serviceUri);
                               var dto = await uri.GetDto(pipeline, cancellationToken);

                               return (name, dto);
                           });
        }

        IAsyncEnumerable<(ApiName, ApiDto)> listAll(CancellationToken cancellationToken) =>
            ApisUri.From(serviceUri)
                   .List(pipeline, cancellationToken);

        bool shouldExtractApiDto(ApiDto dto) =>
            // Don't extract if its version set should not be extracted
            common.ApiModule.TryGetVersionSetName(dto)
                            .Map(shouldExtractVersionSet)
                            .IfNone(true);

        bool shouldExtractVersionSet(VersionSetName name) =>
            findConfigurationVersionSets()
                .Map(names => names.Contains(name))
                .IfNone(true);

        async ValueTask<Option<(ApiSpecification, BinaryData)>> tryGetSpecificationContents(ApiName name, ApiDto dto, CancellationToken cancellationToken)
        {
            var specificationOption = tryGetSpecification(dto);

            return await specificationOption.BindTask(async specification =>
            {
                var uri = ApiUri.From(name, serviceUri);
                var contentsOption = await uri.TryGetSpecificationContents(specification, pipeline, cancellationToken);

                return from contents in contentsOption
                       select (specification, contents);
            });
        }

        Option<ApiSpecification> tryGetSpecification(ApiDto dto) =>
            (dto.Properties.Type ?? dto.Properties.ApiType) switch
            {
                "graphql" => new ApiSpecification.GraphQl(),
                "soap" => new ApiSpecification.Wsdl(),
                "http" => defaultApiSpecification.Value,
                null => defaultApiSpecification.Value,
                _ => Option<ApiSpecification>.None
            };
    }

    private static void ConfigureWriteApiArtifacts(IHostApplicationBuilder builder)
    {
        ConfigureWriteApiInformationFile(builder);
        ConfigureWriteApiSpecificationFile(builder);

        builder.Services.TryAddSingleton(GetWriteApiArtifacts);
    }

    private static WriteApiArtifacts GetWriteApiArtifacts(IServiceProvider provider)
    {
        var writeInformationFile = provider.GetRequiredService<WriteApiInformationFile>();
        var writeSpecificationFile = provider.GetRequiredService<WriteApiSpecificationFile>();

        return async (name, dto, specificationContentsOption, cancellationToken) =>
        {
            await writeInformationFile(name, dto, cancellationToken);

            await specificationContentsOption.IterTask(async x =>
            {
                var (specification, contents) = x;
                await writeSpecificationFile(name, specification, contents, cancellationToken);
            });
        };
    }

    private static void ConfigureWriteApiInformationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteApiInformationFile);
    }

    private static WriteApiInformationFile GetWriteApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = ApiInformationFile.From(name, serviceDirectory);

            logger.LogInformation("Writing API information file {ApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }

    private static void ConfigureWriteApiSpecificationFile(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceDirectory(builder);

        builder.Services.TryAddSingleton(GetWriteApiSpecificationFile);
    }

    private static WriteApiSpecificationFile GetWriteApiSpecificationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (name, specification, contents, cancellationToken) =>
        {
            var specificationFile = ApiSpecificationFile.From(specification, name, serviceDirectory);

            logger.LogInformation("Writing API specification file {ApiSpecificationFile}...", specificationFile);
            await specificationFile.WriteSpecification(contents, cancellationToken);
        };
    }
}