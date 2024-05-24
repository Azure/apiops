using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace integration.tests;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = GetHostBuilder(args);
        using var host = builder.Build();
        await Run(host);
    }

    private static HostApplicationBuilder GetHostBuilder(string[] arguments)
    {
        var builder = Host.CreateApplicationBuilder(arguments);
        Configure(builder);
        return builder;
    }

    private static void Configure(HostApplicationBuilder builder)
    {
        Configure(builder.Configuration);
        Configure(builder.Services);
    }

    private static void Configure(IConfigurationBuilder builder)
    {
        builder.AddUserSecrets(typeof(Program).Assembly); ;
    }

    private static void Configure(IServiceCollection services)
    {
        CommonServices.Configure(services);
        AppServices.ConfigureRunApplication(services);
    }

    private static async Task Run(IHost host)
    {
        var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var cancellationToken = applicationLifetime.ApplicationStopping;

        try
        {
            await host.StartAsync(cancellationToken);
            var runApplication = host.Services.GetRequiredService<RunApplication>();
            await runApplication(cancellationToken);
        }
        catch (Exception exception)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Program));
            logger.LogCritical(exception, "An unhandled exception occurred.");
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}