using Azure.Core;
using Azure.Identity;
using common;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading.Tasks;

namespace publisher;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        await CreateBuilder(arguments).Build().RunAsync();
    }

    private static IHostBuilder CreateBuilder(string[] arguments)
    {
        return Host
            .CreateDefaultBuilder(arguments)
            .ConfigureAppConfiguration(ConfigureConfiguration)
            .ConfigureServices(ConfigureServices);
    }

    private static void ConfigureConfiguration(IConfigurationBuilder builder)
    {
        builder.AddUserSecrets(typeof(Program).Assembly);
        var configuration = builder.Build();
        var yamlPath = configuration.TryGetValue("CONFIGURATION_YAML_PATH");

        if (yamlPath is not null)
        {
            builder.AddYamlFile(yamlPath);
        };
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(GetAzureEnvironment)
                .AddTransient(GetTokenCredential)
                .AddSingleton<AzureHttpClient>()
                .AddHostedService<Publisher>();
    }

    private static AzureEnvironment GetAzureEnvironment(IServiceProvider provider) =>
        provider.GetRequiredService<IConfiguration>().TryGetValue("AZURE_CLOUD_ENVIRONMENT") switch
        {
            null => AzureEnvironment.AzureGlobalCloud,
            nameof(AzureEnvironment.AzureGlobalCloud) => AzureEnvironment.AzureGlobalCloud,
            nameof(AzureEnvironment.AzureChinaCloud) => AzureEnvironment.AzureChinaCloud,
            nameof(AzureEnvironment.AzureUSGovernment) => AzureEnvironment.AzureUSGovernment,
            nameof(AzureEnvironment.AzureGermanCloud) => AzureEnvironment.AzureGermanCloud,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(AzureEnvironment.AzureGlobalCloud)}, {nameof(AzureEnvironment.AzureChinaCloud)}, {nameof(AzureEnvironment.AzureUSGovernment)}, {nameof(AzureEnvironment.AzureGermanCloud)}")
        };

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var token = configuration.TryGetValue("AZURE_BEARER_TOKEN");

        if (token is null)
        {
            var environment = provider.GetRequiredService<AzureEnvironment>();
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                AuthorityHost = new Uri(environment.AuthenticationEndpoint)
            });
        }
        else
        {
            return new StaticTokenCredential(token);
        }
    }
}
