using common;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;

namespace extractor;

internal sealed record DefaultApiSpecification(ApiSpecification Value);

internal static class ApiSpecificationModule
{
    public static void ConfigureDefaultApiSpecification(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetDefaultApiSpecification);
    }

    private static DefaultApiSpecification GetDefaultApiSpecification(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var formatOption = configuration.TryGetValue("API_SPECIFICATION_FORMAT")
                            | configuration.TryGetValue("apiSpecificationFormat");

        var specification = formatOption.Map(format => format switch
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

        return new DefaultApiSpecification(specification);
    }
}
