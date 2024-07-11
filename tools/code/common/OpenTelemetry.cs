using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace common;

public static class OpenTelemetryModule
{
    public static void Configure(IHostApplicationBuilder builder, string activitySourceName)
    {
        builder.Logging.AddOpenTelemetry(Configure);
        Configure(builder.Services, builder.Configuration, activitySourceName);
    }

    private static void Configure(OpenTelemetryLoggerOptions options)
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
    }

    private static void Configure(IServiceCollection services, IConfiguration configuration, string activitySourceName)
    {
#pragma warning disable CA2000 // Dispose objects before losing scope
        services.TryAddSingleton(new ActivitySource(activitySourceName));
#pragma warning restore CA2000 // Dispose objects before losing scope

        services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddHttpClientInstrumentation()
                                               .AddRuntimeInstrumentation()
                                               .AddMeter("*"))
                .WithTracing(tracing => tracing.AddHttpClientInstrumentation()
                                               .AddSource("*")
                                               .SetSampler<AlwaysOnSampler>());

        configuration.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(_ =>
                     {
                         services.AddLogging(builder => builder.AddOpenTelemetry());
                         services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter())
                                 .ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter())
                                 .ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
                     });

        configuration.TryGetValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
                     .Iter(_ => services.AddOpenTelemetry()
                                        .UseAzureMonitor());
    }
}