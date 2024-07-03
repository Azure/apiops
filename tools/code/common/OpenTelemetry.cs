using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace common;

public static class OpenTelemetryServices
{
    public static void Configure(IServiceCollection services)
    {
        var sourceName = services.BuildServiceProvider().GetService<ActivitySource>()?.Name ?? "ApiOps.*";

        services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddHttpClientInstrumentation()
                                               .AddRuntimeInstrumentation()
                                               .AddMeter("Azure.*"))
                .WithTracing(tracing => tracing.AddHttpClientInstrumentation(ConfigureHttpClientTraceInstrumentationOptions)
                                               .AddSource("Azure.*")
                                               .AddSource(sourceName)
                                               .SetSampler<AlwaysOnSampler>());

        var configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();
        configuration.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(_ =>
                     {
                         services.AddLogging(builder => builder.AddOpenTelemetry());
                         services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter())
                                 .ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter())
                                 .ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
                     });
    }

    private static void ConfigureHttpClientTraceInstrumentationOptions(HttpClientTraceInstrumentationOptions options)
    {
        options.FilterHttpRequestMessage = (_) => Activity.Current?.Parent?.Source?.Name != "Azure.Core.Http";
    }
}
