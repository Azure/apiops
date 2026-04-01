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
using System.Diagnostics;
using System.Threading.Tasks;

namespace integration.tests;

internal static class Program
{
    private const string applicationName = "apiops.integration.tests";

    public static async Task Main(string[] args)
    {
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
        ConfigureBuilder(builder, activitySource);

        return builder.Build();
    }

    private static void ConfigureBuilder(IHostApplicationBuilder builder, ActivitySource activitySource)
    {
        builder.Services.TryAddSingleton(activitySource);
        ConfigureConfiguration(builder);
        ConfigureLogging(builder);
        TestsModule.ConfigureRunTests(builder);
    }

    private static void ConfigureConfiguration(IHostApplicationBuilder builder)
    {
        builder.Configuration.AddUserSecrets(typeof(Program).Assembly);
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
    }

    private static async ValueTask RunHost(IHost host)
    {
        var provider = host.Services;
        var applicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>();
        var logger = provider.GetRequiredService<ILogger>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        var runTests = provider.GetRequiredService<RunTests>();

        try
        {
            await host.StartAsync(cancellationToken);
            await runTests(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Integration tests failed.");
            Environment.ExitCode = -1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}