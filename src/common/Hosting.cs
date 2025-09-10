using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System;
using System.Diagnostics;

namespace common;

public static class OpenTelemetryModule
{
    public static void ConfigureBuilder(IHostApplicationBuilder builder)
    {
        builder.Services
               .AddOpenTelemetry()
               .SetDestination(builder.Configuration)
               .WithMetrics(ConfigureMetrics)
               .WithTracing(ConfigureTracing);
    }

    private static IOpenTelemetryBuilder SetDestination(this IOpenTelemetryBuilder builder, IConfiguration configuration)
    {
        configuration.GetValue("APPLICATION_INSIGHTS_CONNECTION_STRING")
                     .Iter(_ =>
                     {
                         if (builder is OpenTelemetryBuilder openTelemetryBuilder)
                         {
                             openTelemetryBuilder.UseAzureMonitor();
                         }
                     });

        configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(_ => builder.UseOtlpExporter());

        return builder;
    }

    private static void ConfigureMetrics(MeterProviderBuilder builder)
    {
        builder.AddHttpClientInstrumentation()
               .AddAspNetCoreInstrumentation();
    }

    private static void ConfigureTracing(TracerProviderBuilder builder)
    {
        builder.SetSampler(new AlwaysOnSampler())
               .AddHttpClientInstrumentation()
               .AddAspNetCoreInstrumentation();
    }
}

public static class HostApplicationBuilderModule
{
    public static IHostApplicationBuilder TryAddSingleton<T>(this IHostApplicationBuilder builder, Func<IServiceProvider, T> factory)
        where T : class
    {
        builder.Services.TryAddSingleton(factory);

        return builder;
    }
}

public static class ActivitySourceModule
{
    public static void ConfigureBuilder(IHostApplicationBuilder builder, string activitySourceName)
    {
        builder.TryAddSingleton(provider => new ActivitySource(activitySourceName));
    }
}