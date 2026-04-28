using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System;
using System.Diagnostics;

namespace common;

public static class OpenTelemetryModule
{
    public static void ConfigureBuilder(IHostApplicationBuilder builder, string applicationName)
    {
        builder.Services
               .AddOpenTelemetry()
               .ConfigureResource(builder => builder.AddService(applicationName))
               .SetDestination(builder.Configuration)
               .WithMetrics(builder => ConfigureMetrics(builder, applicationName))
               .WithTracing(builder => ConfigureTracing(builder, applicationName));
    }

    private static IOpenTelemetryBuilder SetDestination(this IOpenTelemetryBuilder builder, IConfiguration configuration)
    {
        configuration.GetValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
                     .Iter(_ => builder.UseAzureMonitorExporter());

        configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(_ => builder.UseOtlpExporter());

        return builder;
    }

    public static void ConfigureMetrics(MeterProviderBuilder builder, string serviceName)
    {
        builder.AddHttpClientInstrumentation()
               .AddAspNetCoreInstrumentation()
               .SetResourceBuilder(GetResourceBuilder(serviceName));
    }

    public static ResourceBuilder GetResourceBuilder(string serviceName) =>
        ResourceBuilder.CreateDefault().AddService(serviceName);

    public static void ConfigureTracing(TracerProviderBuilder builder, string activitySourceName)
    {
        builder.SetSampler(new AlwaysOnSampler())
               .SetResourceBuilder(GetResourceBuilder(activitySourceName))
               .AddHttpClientInstrumentation()
               .AddAspNetCoreInstrumentation()
               .AddSource(activitySourceName)
               .AddSource("*");
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