using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace extractor;

internal static class Program
{
    private static async Task Main(string[] args) =>
        await HostingModule.RunHost(args, "extractor", ConfigureRunApplication);

    private static void ConfigureRunApplication(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetRunApplication);
    }

    private static RunApplication GetRunApplication(IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            logger.LogInformation("Running extractor...");

            await ValueTask.CompletedTask;

            logger.LogInformation("Extractor finished.");
        };
    }
}