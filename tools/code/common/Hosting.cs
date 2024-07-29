using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public delegate ValueTask RunApplication(CancellationToken cancellationToken);

public static class HostingModule
{
    /// <param name="applicationName">Will be used to set the logger name and the OpenTelemetry activity source name.</param>
    /// <param name="configureRunApplication">Delegate that adds <see cref="common.RunApplication" /> to the builder services.</param>
    /// <returns></returns>
    public static async ValueTask RunHost(string[] arguments, string applicationName, Action<IHostApplicationBuilder> configureRunApplication)
    {
        using var host = GetHost(arguments, applicationName, configureRunApplication);
        await StartHost(host);
        await RunApplication(host);
    }

    private static IHost GetHost(string[] arguments, string applicationName, Action<IHostApplicationBuilder> configureRunApplication)
    {
        var builder = GetBuilder(arguments, applicationName, configureRunApplication);
        return builder.Build();
    }

    private static HostApplicationBuilder GetBuilder(string[] arguments, string applicationName, Action<IHostApplicationBuilder> configureRunApplication)
    {
        var builder = Host.CreateApplicationBuilder(arguments);
        ConfigureBuilder(builder, applicationName, configureRunApplication);
        return builder;
    }

    private static void ConfigureBuilder(HostApplicationBuilder builder, string applicationName, Action<IHostApplicationBuilder> configureRunApplication)
    {
        ConfigureConfiguration(builder);
        OpenTelemetryModule.Configure(builder, applicationName);
        ConfigureLogging(builder, applicationName);
        configureRunApplication(builder);
    }

    private static void ConfigureConfiguration(HostApplicationBuilder builder)
    {
        if (Assembly.GetEntryAssembly() is Assembly entryAssembly)
        {
            builder.Configuration.AddUserSecretsWithLowestPriority(entryAssembly);
        }

        builder.Configuration
               .TryGetValue("CONFIGURATION_YAML_PATH")
               .Iter(path => builder.Configuration.AddYamlFile(path));
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, string applicationName)
    {
        builder.Services.TryAddSingleton(provider => GetCommonLogger(provider, applicationName));
    }

    private static ILogger GetCommonLogger(IServiceProvider provider, string applicationName)
    {
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        return loggerFactory.CreateLogger(applicationName);
    }

    private static async ValueTask StartHost(IHost host)
    {
        var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        await host.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Extracts the delegate <see cref="common.RunApplication"/> from the host's services and runs it.
    /// </summary>
    private static async ValueTask RunApplication(IHost host)
    {
        var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var cancellationToken = applicationLifetime.ApplicationStopping;

        try
        {
            var runApplication = host.Services.GetRequiredService<RunApplication>();
            await runApplication(cancellationToken);
        }
        catch (Exception exception)
        {
            var logger = host.Services.GetRequiredService<ILogger>();
            logger.LogCritical(exception, "Application failed.");
            Environment.ExitCode = -1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}