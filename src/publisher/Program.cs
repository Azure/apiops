using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace publisher;

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

        Console.WriteLine($"Publisher version is {version}.");
    }

    private static IHost CreateHost(string[] arguments)
    {
        var builder = Host.CreateApplicationBuilder(arguments);
        ConfigureBuilder(builder, arguments);

        return builder.Build();
    }

    private static void ConfigureBuilder(IHostApplicationBuilder builder, string[] arguments)
    {
        ConfigureConfiguration(builder, arguments);
        ConfigureLogging(builder);
        ActivitySourceModule.ConfigureBuilder(builder, nameof(publisher));
        OpenTelemetryModule.ConfigureBuilder(builder);
        PublisherModule.ConfigureRunPublisher(builder);
    }

    private static void ConfigureConfiguration(IHostApplicationBuilder builder, string[] arguments)
    {
        var configuration = builder.Configuration;

        // Add user secrets with lowest priority to allow overriding configuration in tests
        configuration.AddWithLowestPriority(builder => builder.AddUserSecrets(typeof(Program).Assembly));

        // Add YAML configuration file if specified
        configuration.GetValue(common.ConfigurationModule.YamlPath)
                     .Iter(path => builder.Configuration.AddYamlFile(path, optional: true, reloadOnChange: true));

        // Add dry-run flag if necessary
        if (configuration.GetValue("DRY_RUN").IsNone)
        {
            var dryRunValue = Option<string>.None();

            // If 'DRY-RUN' is set, use its value
            configuration.GetValue("DRY-RUN")
                         .Iter(value => dryRunValue = value);

            // Otherwise, check the arguments for a `--dry-run` flag
            if (dryRunValue.IsNone)
            {
                if (arguments.Any(value => value.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)))
                {
                    dryRunValue = "true";
                }
            }

            // If we found a dry-run value, add it to the configuration
            dryRunValue.Iter(value =>
            {
                var keyValuePair = KeyValuePair.Create<string, string?>("DRY_RUN", value);

                configuration.AddInMemoryCollection([keyValuePair]);
            });
        }
    }

    private static void ConfigureLogging(IHostApplicationBuilder builder)
    {
        // Azure identity logs are too verbose by default, only log if there's an issue
        builder.Logging.AddFilter("Azure.Identity", LogLevel.Warning);

        builder.TryAddSingleton(provider => provider.GetRequiredService<ILoggerFactory>()
                                                    .CreateLogger(nameof(publisher)));
    }

    private static async ValueTask RunHost(IHost host)
    {
        var provider = host.Services;
        var applicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        var configuration = provider.GetRequiredService<IConfiguration>();
        var runPublisher = provider.GetRequiredService<RunPublisher>();
        var logger = provider.GetRequiredService<ILogger>();

        try
        {
            await common.ConfigurationModule.Log(configuration, Console.Out, cancellationToken);
            await host.StartAsync(cancellationToken);
            await runPublisher(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Publisher failed. Please check the logs for more details.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}