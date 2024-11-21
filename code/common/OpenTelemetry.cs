using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using System.Diagnostics;

namespace common;

public static class OpenTelemetryModule
{
    public static void Configure(IHostApplicationBuilder builder, string activitySourceName)
    {
        ConfigureActivitySource(builder.Services, activitySourceName);
        ConfigureTelemetry(builder.Services, builder.Configuration);
    }

    private static void ConfigureActivitySource(IServiceCollection services, string activitySourceName)
    {
#pragma warning disable CA2000 // Dispose objects before losing scope
        // Will be disposed when the service collection is disposed.
        var activitySource = new ActivitySource(activitySourceName);
#pragma warning restore CA2000 // Dispose objects before losing scope
        services.TryAddSingleton(activitySource);
    }

    private static void ConfigureTelemetry(IServiceCollection services, IConfiguration configuration)
    {
        var openTelemetryBuilder = services.AddOpenTelemetry();

        configuration.GetValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
                     .Match(_ => configureAzureMonitorExporter(),
                            configureOtlpExporter);

        void configureAzureMonitorExporter() =>
            openTelemetryBuilder.UseAzureMonitor();

        void configureOtlpExporter() =>
            configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                         .Iter(_ => openTelemetryBuilder.UseOtlpExporter()
                                                        .WithMetrics(builder => builder.AddMeter("*"))
                                                        .WithTracing(builder => builder.AddSource("*")));
    }
}