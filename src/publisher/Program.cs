using Azure.Monitor.OpenTelemetry.Exporter;
using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace publisher;

#pragma warning disable CA1515 // Consider making public types internal
public static class Program
#pragma warning restore CA1515 // Consider making public types internal
{
    private const string applicationName = "apiops.publisher";

    public static async Task Main(string[] args)
    {
        LogVersion();

        using var activitySource = new ActivitySource(applicationName);
        using var tracerProvider = GetTracerProvider();
        using var meterProvider = GetMeterProvider();

        using var activity = activitySource.StartActivity(applicationName, ActivityKind.Server);
        try
        {
            using var host = await CreateHost(args, activitySource);
            await RunHost(host);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.ToString());
            throw;
        }
    }

    private static void LogVersion()
    {
        var version = Assembly.GetExecutingAssembly()
                              .GetName()
                              .Version
                             ?.ToString(3) ?? "unknown";

        Console.WriteLine($"Publisher version is {version}.");
    }

    private static TracerProvider GetTracerProvider()
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        OpenTelemetryModule.ConfigureTracing(builder, applicationName);

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables()
                                                      .Build();

        configuration.GetValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
                     .Iter(_ => builder.AddAzureMonitorTraceExporter());

        configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(_ => builder.AddOtlpExporter());

        return builder.Build();
    }

    private static MeterProvider GetMeterProvider()
    {
        var builder = Sdk.CreateMeterProviderBuilder();
        OpenTelemetryModule.ConfigureMetrics(builder, applicationName);

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables()
                                                      .Build();

        configuration.GetValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
                     .Iter(_ => builder.AddAzureMonitorMetricExporter());

        configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(_ => builder.AddOtlpExporter());

        return builder.Build();
    }

    private static async ValueTask<IHost> CreateHost(string[] arguments, ActivitySource activitySource)
    {
        var builder = Host.CreateApplicationBuilder(arguments);
        ConfigureBuilder(builder, arguments, activitySource);

        return builder.Build();
    }

    private static void ConfigureBuilder(IHostApplicationBuilder builder, string[] arguments, ActivitySource activitySource)
    {
        builder.Services.TryAddSingleton(activitySource);
        ConfigureConfiguration(builder, arguments);
        ConfigureLogging(builder);
        PublisherModule.ConfigureRunPublisher(builder);
    }

    private static void ConfigureLogging(IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.SetResourceBuilder(OpenTelemetryModule.GetResourceBuilder(applicationName));

            var configuration = builder.Configuration;

            configuration.GetValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
                         .Iter(_ => options.AddAzureMonitorLogExporter());

            configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                         .Iter(_ => options.AddOtlpExporter());
        });

        builder.TryAddSingleton(provider => provider.GetRequiredService<ILoggerFactory>()
                                                    .CreateLogger(applicationName));

        // Azure identity logs are too verbose by default, only log if there's an issue
        builder.Logging.AddFilter("Azure.Identity", LogLevel.Warning);
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