using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace integration.tests;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        using var host = CreateHost(args);
        await RunHost(host);
    }

    private static IHost CreateHost(string[] arguments)
    {
        var builder = Host.CreateApplicationBuilder(arguments);
        ConfigureBuilder(builder);

        return builder.Build();
    }

    private static void ConfigureBuilder(IHostApplicationBuilder builder)
    {
        ConfigureConfiguration(builder);
        ConfigureLogging(builder);
        ActivitySourceModule.ConfigureBuilder(builder, "integration.tests");
        OpenTelemetryModule.ConfigureBuilder(builder);
        IntegrationTestsModule.ConfigureRunIntegrationTests(builder);
    }

    private static void ConfigureConfiguration(IHostApplicationBuilder builder)
    {
        builder.Configuration.AddUserSecrets(typeof(Program).Assembly);
    }

    private static void ConfigureLogging(IHostApplicationBuilder builder)
    {
        // Azure identity logs are too verbose by default, only log if there's an issue
        // builder.Logging.AddFilter("Azure.Identity", LogLevel.Warning);

        builder.TryAddSingleton(provider => provider.GetRequiredService<ILoggerFactory>()
                                                    .CreateLogger("integration.tests"));
    }

    private static async ValueTask RunHost(IHost host)
    {
        var provider = host.Services;
        var applicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>();
        var logger = provider.GetRequiredService<ILogger>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        var runIntegrationTests = provider.GetRequiredService<RunIntegrationTests>();

        try
        {
            await host.StartAsync(cancellationToken);
            await runIntegrationTests(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Integration tests failed. Please check the logs for more details.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}