using common;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace common;

public static class PolicyContentFormatModule
{
    public static void ConfigureDefaultPolicyContentFormat(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetDefaultPolicyContentFormat);
    }

    private static PolicyContentFormat GetDefaultPolicyContentFormat(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var formatOption = configuration.TryGetValue("POLICY_CONTENT_FORMAT")
                            | configuration.TryGetValue("policyContentFormat");

        var specification = formatOption.Map(format => format switch
        {
            var value when "RAWXML".Equals(value, StringComparison.OrdinalIgnoreCase) => new PolicyContentFormat.RawXml() as PolicyContentFormat,
            var value when "XML".Equals(value, StringComparison.OrdinalIgnoreCase) => new PolicyContentFormat.Xml(),
            var value => throw new NotSupportedException($"Policy content format '{value}' defined in configuration is not supported.")
        }).IfNone(() => new PolicyContentFormat.RawXml());

        return specification;
    }
}

//public class PolicyContentFormatJsonConverter : JsonConverter<PolicyContentFormat>
//{
//    public override PolicyContentFormat Read(
//        ref Utf8JsonReader reader,
//        Type typeToConvert,
//        JsonSerializerOptions options)
//    {
//        return reader.GetString() switch
//        {
//            "rawxml" => new PolicyContentFormat.RawXml(),
//            "xml" => new PolicyContentFormat.Xml(),
//            "rawxml-link" => new PolicyContentFormat.RawXmlLink(),
//            "xml-link" => new PolicyContentFormat.XmlLink(),
//            _ => throw new NotSupportedException()
//        };
//    }

//    public override void Write(
//        Utf8JsonWriter writer,
//        PolicyContentFormat value, 
//        JsonSerializerOptions options)
//    {
//        // switch on PolicyContentFormat to determine the string to render
//        writer.WriteStringValue(value.GetPolicyContentFormat);
//    }
//}
