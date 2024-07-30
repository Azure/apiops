using common;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;

namespace extractor;

internal sealed record DefaultPolicySpecification(string PolicyFormat = "rawxml");

internal static class PolicySpecificationModule
{
    public static void ConfigureDefaultPolicySpecification(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetDefaultPolicySpecification);
    }

    private static DefaultPolicySpecification GetDefaultPolicySpecification(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var formatOption = configuration.TryGetValue("POLICY_SPECIFICATION_FORMAT")
                            | configuration.TryGetValue("policySpecificationFormat");

        var specification = formatOption.Map(format => format switch
        {
            var value when "RAWXML".Equals(value, StringComparison.OrdinalIgnoreCase) => new DefaultPolicySpecification("rawxml"),
            var value when "XML".Equals(value, StringComparison.OrdinalIgnoreCase) => new DefaultPolicySpecification("xml"),
            var value => throw new NotSupportedException($"Policy specification format '{value}' defined in configuration is not supported.")
        }).IfNone(() => new DefaultPolicySpecification("rawxml"));

        return specification;
    }
}
