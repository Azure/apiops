using Azure.Core.Pipeline;
using common;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace extractor;

internal delegate ValueTask ExtractApis(CancellationToken cancellationToken);

file delegate IAsyncEnumerable<(ApiName Name, ApiDto Dto, Option<(ApiSpecification Specification, BinaryData Contents)> SpecificationOption)> ListApis(CancellationToken cancellationToken);

internal delegate bool ShouldExtractApiName(ApiName name);

file delegate bool ShouldExtractApiDto(ApiDto dto);

file delegate ValueTask WriteApiArtifacts(ApiName name, ApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, CancellationToken cancellationToken);

file delegate ValueTask WriteApiInformationFile(ApiName name, ApiDto dto, CancellationToken cancellationToken);

file delegate ValueTask WriteApiSpecificationFile(ApiName name, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken);

file sealed class ExtractApisHandler(ListApis list,
                                     ShouldExtractApiName shouldExtractName,
                                     ShouldExtractApiDto shouldExtractDto,
                                     WriteApiArtifacts writeArtifacts,
                                     ExtractApiPolicies extractApiPolicies,
                                     ExtractApiTags extractApiTags,
                                     ExtractApiOperations extractApiOperations)
{
    public async ValueTask Handle(CancellationToken cancellationToken) =>
        await list(cancellationToken)
                .Where(api => shouldExtractName(api.Name))
                .Where(api => shouldExtractDto(api.Dto))
                // Group APIs by version set (https://github.com/Azure/apiops/issues/316).
                // We'll process each group in parallel, but each API within a group sequentially.
                .GroupBy(api => api.Dto.Properties.ApiVersionSetId ?? Guid.NewGuid().ToString())
                .IterParallel(async group => await group.Iter(async api => await ExtractApi(api.Name, api.Dto, api.SpecificationOption, cancellationToken),
                                                                cancellationToken),
                                cancellationToken);

    private async ValueTask ExtractApi(ApiName name, ApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, CancellationToken cancellationToken)
    {
        await writeArtifacts(name, dto, specificationOption, cancellationToken);
        await extractApiPolicies(name, cancellationToken);
        await extractApiTags(name, cancellationToken);
        await extractApiOperations(name, cancellationToken);
    }
}

file sealed class ListApisHandler(ManagementServiceUri serviceUri, HttpPipeline pipeline, IConfiguration configuration)
{
    private readonly ApiSpecification defaultApiSpecification = GetDefaultApiSpecification(configuration);

    public IAsyncEnumerable<(ApiName, ApiDto, Option<(ApiSpecification, BinaryData)>)> Handle(CancellationToken cancellationToken) =>
        ApisUri.From(serviceUri)
               .List(pipeline, cancellationToken)
               .SelectAwait(async api =>
               {
                   var (name, dto) = api;
                   var specificationContentsOption = await TryGetSpecificationContents(name, dto, cancellationToken);
                   return (name, dto, specificationContentsOption);
               });

    private async ValueTask<Option<(ApiSpecification, BinaryData)>> TryGetSpecificationContents(ApiName name, ApiDto dto, CancellationToken cancellationToken)
    {
        var specificationOption = TryGetSpecification(dto);

        return await specificationOption.BindTask(async specification =>
        {
            var uri = ApiUri.From(name, serviceUri);
            var contentsOption = await uri.TryGetSpecificationContents(specification, pipeline, cancellationToken);

            return from contents in contentsOption
                   select (specification, contents);
        });
    }

    private static ApiSpecification GetDefaultApiSpecification(IConfiguration configuration)
    {
        var formatOption = configuration.TryGetValue("API_SPECIFICATION_FORMAT")
                            | configuration.TryGetValue("apiSpecificationFormat");

        return formatOption.Map(format => format switch
        {
            var value when "Wadl".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.Wadl() as ApiSpecification,
            var value when "JSON".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.OpenApi
            {
                Format = new OpenApiFormat.Json(),
                Version = new OpenApiVersion.V3()
            },
            var value when "YAML".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.OpenApi
            {
                Format = new OpenApiFormat.Yaml(),
                Version = new OpenApiVersion.V3()
            },
            var value when "OpenApiV2Json".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.OpenApi
            {
                Format = new OpenApiFormat.Json(),
                Version = new OpenApiVersion.V2()
            },
            var value when "OpenApiV2Yaml".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.OpenApi
            {
                Format = new OpenApiFormat.Yaml(),
                Version = new OpenApiVersion.V2()
            },
            var value when "OpenApiV3Json".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.OpenApi
            {
                Format = new OpenApiFormat.Json(),
                Version = new OpenApiVersion.V3()
            },
            var value when "OpenApiV3Yaml".Equals(value, StringComparison.OrdinalIgnoreCase) => new ApiSpecification.OpenApi
            {
                Format = new OpenApiFormat.Yaml(),
                Version = new OpenApiVersion.V3()
            },
            var value => throw new NotSupportedException($"API specification format '{value}' defined in configuration is not supported.")
        }).IfNone(() => new ApiSpecification.OpenApi
        {
            Format = new OpenApiFormat.Yaml(),
            Version = new OpenApiVersion.V3()
        });
    }

    private Option<ApiSpecification> TryGetSpecification(ApiDto dto) =>
        (dto.Properties.Type ?? dto.Properties.ApiType) switch
        {
            "graphql" => new ApiSpecification.GraphQl(),
            "soap" => new ApiSpecification.Wsdl(),
            "http" => defaultApiSpecification,
            null => defaultApiSpecification,
            _ => Option<ApiSpecification>.None
        };
}

file sealed class ShouldExtractApiNameHandler(ShouldExtractFactory shouldExtractFactory)
{
    public bool Handle(ApiName name)
    {
        var shouldExtract = shouldExtractFactory.Create<ApiName>();
        return shouldExtract(name);
    }
}

file sealed class ShouldExtractApiDtoHandler(ShouldExtractVersionSet shouldExtractVersionSet)
{
    public bool Handle(ApiDto dto) =>
        // Don't extract if its version set should not be extracted
        ApiModule.TryGetVersionSetName(dto)
                 .Map(shouldExtractVersionSet.Invoke)
                 .IfNone(true);
}

file sealed class WriteApiArtifactsHandler(WriteApiInformationFile writeInformationFile,
                                           WriteApiSpecificationFile writeSpecificationFile)
{
    public async ValueTask Handle(ApiName name, ApiDto dto, Option<(ApiSpecification, BinaryData)> specificationContentsOption, CancellationToken cancellationToken)
    {
        await writeInformationFile(name, dto, cancellationToken);

        await specificationContentsOption.IterTask(async x =>
        {
            var (specification, contents) = x;
            await writeSpecificationFile(name, specification, contents, cancellationToken);
        });
    }
}

file sealed class WriteApiInformationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, ApiDto dto, CancellationToken cancellationToken)
    {
        var informationFile = ApiInformationFile.From(name, serviceDirectory);

        logger.LogInformation("Writing API information file {ApiInformationFile}...", informationFile);
        await informationFile.WriteDto(dto, cancellationToken);
    }
}

file sealed class WriteApiSpecificationFileHandler(ILoggerFactory loggerFactory, ManagementServiceDirectory serviceDirectory)
{
    private readonly ILogger logger = Common.GetLogger(loggerFactory);

    public async ValueTask Handle(ApiName name, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken)
    {
        var specificationFile = ApiSpecificationFile.From(specification, name, serviceDirectory);

        logger.LogInformation("Writing API specification file {ApiSpecificationFile}...", specificationFile);
        await specificationFile.WriteSpecification(contents, cancellationToken);
    }
}

internal static class ApiServices
{
    public static void ConfigureExtractApis(IServiceCollection services)
    {
        ConfigureListApis(services);
        ConfigureShouldExtractApiName(services);
        ConfigureShouldExtractApiDto(services);
        ConfigureWriteApiArtifacts(services);
        ApiPolicyServices.ConfigureExtractApiPolicies(services);
        ApiTagServices.ConfigureExtractApiTags(services);
        ApiOperationServices.ConfigureExtractApiOperations(services);

        services.TryAddSingleton<ExtractApisHandler>();
        services.TryAddSingleton<ExtractApis>(provider => provider.GetRequiredService<ExtractApisHandler>().Handle);
    }

    private static void ConfigureListApis(IServiceCollection services)
    {
        services.TryAddSingleton<ListApisHandler>();
        services.TryAddSingleton<ListApis>(provider => provider.GetRequiredService<ListApisHandler>().Handle);
    }

    public static void ConfigureShouldExtractApiName(IServiceCollection services)
    {
        services.TryAddSingleton<ShouldExtractApiNameHandler>();
        services.TryAddSingleton<ShouldExtractApiName>(provider => provider.GetRequiredService<ShouldExtractApiNameHandler>().Handle);
    }

    public static void ConfigureShouldExtractApiDto(IServiceCollection services)
    {
        VersionSetServices.ConfigureShouldExtractVersionSet(services);

        services.TryAddSingleton<ShouldExtractApiDtoHandler>();
        services.TryAddSingleton<ShouldExtractApiDto>(provider => provider.GetRequiredService<ShouldExtractApiDtoHandler>().Handle);
    }

    private static void ConfigureWriteApiArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiInformationFile(services);
        ConfigureWriteApiSpecificationFile(services);

        services.TryAddSingleton<WriteApiArtifactsHandler>();
        services.TryAddSingleton<WriteApiArtifacts>(provider => provider.GetRequiredService<WriteApiArtifactsHandler>().Handle);
    }

    private static void ConfigureWriteApiInformationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteApiInformationFileHandler>();
        services.TryAddSingleton<WriteApiInformationFile>(provider => provider.GetRequiredService<WriteApiInformationFileHandler>().Handle);
    }

    private static void ConfigureWriteApiSpecificationFile(IServiceCollection services)
    {
        services.TryAddSingleton<WriteApiSpecificationFileHandler>();
        services.TryAddSingleton<WriteApiSpecificationFile>(provider => provider.GetRequiredService<WriteApiSpecificationFileHandler>().Handle);
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
         loggerFactory.CreateLogger("ApiExtractor");
}