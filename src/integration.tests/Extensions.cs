using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;

namespace integration.tests;

internal static class HostApplicationBuilderExtensions
{
    public static IHostApplicationBuilder AddServiceDirectoryToConfiguration(this IHostApplicationBuilder builder, ServiceDirectory serviceDirectory)
    {
        builder.Configuration.AddInMemoryCollection([KeyValuePair.Create("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH", (string?)serviceDirectory.ToString())]);

        return builder;
    }
}