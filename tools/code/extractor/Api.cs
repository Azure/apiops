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

internal delegate IAsyncEnumerable<(ApiName Name, ApiDto Dto, Option<(ApiSpecification Specification, BinaryData Contents)> SpecificationOption)> ListApis(CancellationToken cancellationToken);

internal delegate bool ShouldExtractApiName(ApiName name);

internal delegate bool ShouldExtractApiDto(ApiDto dto);

internal delegate ValueTask WriteApiArtifacts(ApiName name, ApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiInformationFile(ApiName name, ApiDto dto, CancellationToken cancellationToken);

internal delegate ValueTask WriteApiSpecificationFile(ApiName name, ApiSpecification specification, BinaryData contents, CancellationToken cancellationToken);

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

        services.TryAddSingleton(ExtractApis);
    }

    private static ExtractApis ExtractApis(IServiceProvider provider)
    {
        var list = provider.GetRequiredService<ListApis>();
        var shouldExtractName = provider.GetRequiredService<ShouldExtractApiName>();
        var shouldExtractDto = provider.GetRequiredService<ShouldExtractApiDto>();
        var writeArtifacts = provider.GetRequiredService<WriteApiArtifacts>();
        var extractApiPolicies = provider.GetRequiredService<ExtractApiPolicies>();
        var extractApiTags = provider.GetRequiredService<ExtractApiTags>();
        var extractApiOperations = provider.GetRequiredService<ExtractApiOperations>();

        return async cancellationToken =>
            await list(cancellationToken)
                    .Where(api => shouldExtractName(api.Name))
                    .Where(api => shouldExtractDto(api.Dto))
                    // Group APIs by version set (https://github.com/Azure/apiops/issues/316).
                    // We'll process each group in parallel, but each API within a group sequentially.
                    .GroupBy(api => api.Dto.Properties.ApiVersionSetId ?? string.Empty)
                    .IterParallel(async group => await group.Iter(async api => await extractApi(api.Name, api.Dto, api.SpecificationOption, cancellationToken),
                                                                  cancellationToken),
                                  cancellationToken);

        async ValueTask extractApi(ApiName name, ApiDto dto, Option<(ApiSpecification Specification, BinaryData Contents)> specificationOption, CancellationToken cancellationToken)
        {
            await writeArtifacts(name, dto, specificationOption, cancellationToken);
            await extractApiPolicies(name, cancellationToken);
            await extractApiTags(name, cancellationToken);
            await extractApiOperations(name, cancellationToken);
        }
    }

    private static void ConfigureListApis(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceUri(services);
        CommonServices.ConfigureHttpPipeline(services);

        services.TryAddSingleton(ListApis);
    }

    private static ListApis ListApis(IServiceProvider provider)
    {
        var serviceUri = provider.GetRequiredService<ManagementServiceUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var defaultApiSpecification = getDefaultApiSpecification(configuration);

        return cancellationToken =>
            ApisUri.From(serviceUri)
                   .List(pipeline, cancellationToken)
                   .SelectAwait(async api =>
                   {
                       var (name, dto) = api;
                       var specificationContentsOption = await tryGetSpecificationContents(name, dto, cancellationToken);
                       return (name, dto, specificationContentsOption);
                   });

        static ApiSpecification getDefaultApiSpecification(IConfiguration configuration)
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
                "http" => defaultApiSpecification,
                null => defaultApiSpecification,
                _ => Option<ApiSpecification>.None
            };
    }

    public static void ConfigureShouldExtractApiName(IServiceCollection services)
    {
        CommonServices.ConfigureShouldExtractFactory(services);

        services.TryAddSingleton(ShouldExtractApiName);
    }

    private static ShouldExtractApiName ShouldExtractApiName(IServiceProvider provider)
    {
        var shouldExtractFactory = provider.GetRequiredService<ShouldExtractFactory>();

        var shouldExtract = shouldExtractFactory.Create<ApiName>();

        return name => shouldExtract(name);
    }

    public static void ConfigureShouldExtractApiDto(IServiceCollection services)
    {
        services.TryAddSingleton(ShouldExtractApiDto);
    }

    private static ShouldExtractApiDto ShouldExtractApiDto(IServiceProvider provider)
    {
        var shouldExtractVersionSet = provider.GetRequiredService<ShouldExtractVersionSet>();

        return dto =>
            // Don't extract if its version set should not be extracted
            ApiModule.TryGetVersionSetName(dto)
                     .Map(shouldExtractVersionSet.Invoke)
                     .IfNone(true);
    }

    private static void ConfigureWriteApiArtifacts(IServiceCollection services)
    {
        ConfigureWriteApiInformationFile(services);
        ConfigureWriteApiSpecificationFile(services);

        services.TryAddSingleton(WriteApiArtifacts);
    }

    private static WriteApiArtifacts WriteApiArtifacts(IServiceProvider provider)
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

    private static void ConfigureWriteApiInformationFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteApiInformationFile);
    }

    private static WriteApiInformationFile WriteApiInformationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, dto, cancellationToken) =>
        {
            var informationFile = ApiInformationFile.From(name, provider.GetRequiredService<ManagementServiceDirectory>());

            logger.LogInformation("Writing API information file {ApiInformationFile}...", informationFile);
            await informationFile.WriteDto(dto, cancellationToken);
        };
    }

    private static void ConfigureWriteApiSpecificationFile(IServiceCollection services)
    {
        CommonServices.ConfigureManagementServiceDirectory(services);

        services.TryAddSingleton(WriteApiSpecificationFile);
    }

    private static WriteApiSpecificationFile WriteApiSpecificationFile(IServiceProvider provider)
    {
        var serviceDirectory = provider.GetRequiredService<ManagementServiceDirectory>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var logger = Common.GetLogger(loggerFactory);

        return async (name, specification, contents, cancellationToken) =>
        {
            var specificationFile = ApiSpecificationFile.From(specification, name, serviceDirectory);

            logger.LogInformation("Writing API specification file {ApiSpecificationFile}...", specificationFile);
            await specificationFile.WriteSpecification(contents, cancellationToken);
        };
    }
}

file static class Common
{
    public static ILogger GetLogger(ILoggerFactory loggerFactory) =>
         loggerFactory.CreateLogger("ApiExtractor");
}