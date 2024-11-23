using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace extractor;

#pragma warning disable CA1515 // Consider making public types internal
public static class Program
#pragma warning restore CA1515 // Consider making public types internal
{
    public static async Task Main(string[] args) =>
        await HostingModule.RunHost(args, "extractor", ConfigureRunApplication);

    private static void ConfigureRunApplication(IHostApplicationBuilder builder)
    {
        NamedValueModule.ConfigureExtractNamedValues(builder);

        builder.Services.TryAddSingleton(GetRunApplication);
    }

    private static RunApplication GetRunApplication(IServiceProvider provider)
    {
        var extractNamedValues = provider.GetRequiredService<ExtractNamedValues>();

        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            logger.LogInformation("Running extractor...");

            await extractNamedValues(cancellationToken);

            logger.LogInformation("Extractor finished.");
        };
    }
}