using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace integration.tests;

internal static class Program
{
    private static async Task Main(string[] args) =>
        await HostingModule.RunHost(args, "integration.tests", ConfigureRunApplication);

    private static void ConfigureRunApplication(IHostApplicationBuilder builder)
    {
        ServiceModule.ConfigureDeleteAllTestServices(builder);
        TestModule.ConfigureTestExtract(builder);

        builder.Services.TryAddSingleton(GetRunApplication);
    }

    private static RunApplication GetRunApplication(IServiceProvider provider)
    {
        var deleteAllTestServices = provider.GetRequiredService<DeleteAllTestServices>();
        var testExtract = provider.GetRequiredService<TestExtract>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            logger.LogInformation($"Running integration tests...");

            await deleteAllTestServices(cancellationToken);
            await testExtract(cancellationToken);

            logger.LogInformation($"Integration tests finished.");
        };
    }
}