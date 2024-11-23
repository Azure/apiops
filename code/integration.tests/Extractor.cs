using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask RunExtractor(ServiceName name, ServiceDirectory directory, CancellationToken cancellationToken);

internal static class ExtractorModule
{
    public static void ConfigureRunExtractor(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetRunExtractor);
    }

    private static RunExtractor GetRunExtractor(IServiceProvider provider)
    {
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async (name, directory, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity("extractor.run")
                                       ?.AddTag("service_name", name)
                                       ?.AddTag("service_directory", directory);

            var arguments = getArguments(name, directory);

            await extractor.Program.Main(arguments);
        };

        static string[] getArguments(ServiceName name, ServiceDirectory directory) =>
            new Dictionary<string, string>
            {
                ["API_MANAGEMENT_SERVICE_NAME"] = name.ToString(),
                ["API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH"] = directory.ToDirectoryInfo().FullName
            }
            .Aggregate(Array.Empty<string>(),
                       (arguments, kvp) => [.. arguments, $"--{kvp.Key}", kvp.Value]);
    }
}
