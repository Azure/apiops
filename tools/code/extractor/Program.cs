using common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace extractor;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        var host = Host.CreateDefaultBuilder(arguments)
                       .ConfigureAppConfiguration(ConfigureConfiguration)
                       .ConfigureServices(ConfigureServices)
                       .Build();

        await RunExtractor(host);
    }

    private static void ConfigureConfiguration(IConfigurationBuilder builder)
    {
        builder.AddUserSecrets(typeof(Program).Assembly);

        var configuration = builder.Build();

        configuration.TryGetValue("CONFIGURATION_YAML_PATH")
                     .Iter(path => builder.AddYamlFile(path));
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        CommonServices.Configure(services);
        AppServices.ConfigureRunExtractor(services);
    }

    private static async Task RunExtractor(IHost host)
    {
        var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        await host.StartAsync(cancellationToken);

        try
        {
            var runExtractor = host.Services.GetRequiredService<RunExtractor>();
            await runExtractor(cancellationToken);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = -1;
            LogException(host, exception);
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }

    private static void LogException(IHost host, Exception exception)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Program));
        logger.LogCritical(exception, "Extractor failed with error {ErrorMessage}", exception.Message);
    }
}
