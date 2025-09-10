using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace extractor;

#pragma warning disable CA1515 // Consider making public types internal
public static class Program
#pragma warning restore CA1515 // Consider making public types internal
{
    public static async Task Main(string[] args)
    {
        LogVersion();
        using var host = CreateHost(args);
        await RunHost(host);
    }

    private static void LogVersion()
    {
        var version = Assembly.GetExecutingAssembly()
                              .GetName()
                              .Version
                             ?.ToString(3) ?? "unknown";

        Console.WriteLine($"Extractor version is {version}.");
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
        ActivitySourceModule.ConfigureBuilder(builder, nameof(extractor));
        OpenTelemetryModule.ConfigureBuilder(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
    }

    private static void ConfigureConfiguration(IHostApplicationBuilder builder)
    {
        builder.Configuration
               .GetValue(common.ConfigurationModule.YamlPath)
               .Iter(path => builder.Configuration.AddYamlFile(path, optional: true, reloadOnChange: true));

        // Add user secrets with lowest priority to allow overriding configuration in tests
        builder.Configuration.AddWithLowestPriority(builder => builder.AddUserSecrets(typeof(Program).Assembly));
    }

    private static void ConfigureLogging(IHostApplicationBuilder builder)
    {
        // Azure identity logs are too verbose by default, only log if there's an issue
        builder.Logging.AddFilter("Azure.Identity", LogLevel.Warning);

        builder.TryAddSingleton(provider => provider.GetRequiredService<ILoggerFactory>()
                                                    .CreateLogger(nameof(extractor)));
    }

    private static async ValueTask RunHost(IHost host)
    {
        var provider = host.Services;
        var applicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>();
        var logger = provider.GetRequiredService<ILogger>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        var runExtractor = provider.GetRequiredService<RunExtractor>();

        try
        {
            await host.StartAsync(cancellationToken);
            await runExtractor(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Extractor failed. Please check the logs for more details.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}