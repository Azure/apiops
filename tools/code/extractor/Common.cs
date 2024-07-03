using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using common;
using Flurl;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace extractor;

internal static class CommonServices
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton(GetActivitySource);
        services.AddSingleton(GetAzureEnvironment);
        services.AddSingleton(GetTokenCredential);
        services.AddSingleton(GetHttpPipeline);
        services.AddSingleton(GetManagementServiceName);
        services.AddSingleton(GetManagementServiceUri);
        services.AddSingleton(GetManagementServiceDirectory);
        services.AddSingleton(GetConfigurationJson);
        services.AddSingleton<ShouldExtractFactory>();
        OpenTelemetryServices.Configure(services);
    }

    private static ActivitySource GetActivitySource(IServiceProvider provider) =>
        new("ApiOps.Extractor");

    private static AzureEnvironment GetAzureEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT").ValueUnsafe() switch
        {
            null => AzureEnvironment.Public,
            "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => AzureEnvironment.Public,
            "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => AzureEnvironment.China,
            "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => AzureEnvironment.USGovernment,
            "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => AzureEnvironment.Germany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
        };
    }

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var azureAuthorityHost = provider.GetRequiredService<AzureEnvironment>().AuthorityHost;

        return configuration.TryGetValue("AZURE_BEARER_TOKEN")
                            .Map(GetCredentialFromToken)
                            .IfNone(() => GetDefaultAzureCredential(azureAuthorityHost));
    }

    private static TokenCredential GetCredentialFromToken(string token)
    {
        var jsonWebToken = new JsonWebToken(token);
        var expirationDate = new DateTimeOffset(jsonWebToken.ValidTo);
        var accessToken = new AccessToken(token, expirationDate);

        return DelegatedTokenCredential.Create((context, cancellationToken) => accessToken);
    }

    private static DefaultAzureCredential GetDefaultAzureCredential(Uri azureAuthorityHost) =>
        new(new DefaultAzureCredentialOptions
        {
            AuthorityHost = azureAuthorityHost
        });

    public static void ConfigureHttpPipeline(IServiceCollection services)
    {
        services.TryAddSingleton(GetHttpPipeline);
    }

    private static HttpPipeline GetHttpPipeline(IServiceProvider provider)
    {
        var clientOptions = ClientOptions.Default;
        clientOptions.RetryPolicy = new CommonRetryPolicy();

        var tokenCredential = provider.GetRequiredService<TokenCredential>();
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var bearerAuthenticationPolicy = new BearerTokenAuthenticationPolicy(tokenCredential, azureEnvironment.DefaultScope);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(HttpPipeline));
        var loggingPolicy = new ILoggerHttpPipelinePolicy(logger);

        var version = Assembly.GetExecutingAssembly()?.GetName().Version ?? new Version("-1");
        var telemetryPolicy = new TelemetryPolicy(version);

        return HttpPipelineBuilder.Build(clientOptions, bearerAuthenticationPolicy, loggingPolicy, telemetryPolicy);
    }

    private static ManagementServiceName GetManagementServiceName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var name = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME")
                                .IfNone(() => configuration.GetValue("apimServiceName"));

        return ManagementServiceName.From(name);
    }

    public static void ConfigureManagementServiceUri(IServiceCollection services)
    {
        services.TryAddSingleton(GetManagementServiceUri);
    }

    private static ManagementServiceUri GetManagementServiceUri(IServiceProvider provider)
    {
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var serviceName = provider.GetRequiredService<ManagementServiceName>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var apiVersion = configuration.TryGetValue("ARM_API_VERSION")
                                      .IfNone(() => "2022-08-01");

        var uri = azureEnvironment.ManagementEndpoint
                                  .AppendPathSegment("subscriptions")
                                  .AppendPathSegment(configuration.GetValue("AZURE_SUBSCRIPTION_ID"))
                                  .AppendPathSegment("resourceGroups")
                                  .AppendPathSegment(configuration.GetValue("AZURE_RESOURCE_GROUP_NAME"))
                                  .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                  .AppendPathSegment(serviceName.ToString())
                                  .SetQueryParam("api-version", apiVersion)
                                  .ToUri();

        return ManagementServiceUri.From(uri);
    }

    public static void ConfigureManagementServiceDirectory(IServiceCollection services)
    {
        services.TryAddSingleton(GetManagementServiceDirectory);
    }

    private static ManagementServiceDirectory GetManagementServiceDirectory(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var directoryPath = configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH");
        var directory = new DirectoryInfo(directoryPath);

        return ManagementServiceDirectory.From(directory);
    }

    public static void ConfigureShouldExtractFactory(IServiceCollection services)
    {
        ConfigureConfigurationJson(services);

        services.TryAddSingleton(GetShouldExtractFactory);
    }

    private static ShouldExtractFactory GetShouldExtractFactory(IServiceProvider provider)
    {
        var configurationJson = provider.GetRequiredService<ConfigurationJson>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        return new ShouldExtractFactory(configurationJson, loggerFactory);
    }

    private static void ConfigureConfigurationJson(IServiceCollection services)
    {
        services.TryAddSingleton(GetConfigurationJson);
    }

    private static ConfigurationJson GetConfigurationJson(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var configurationJson = ConfigurationJson.From(configuration);

        return GetConfigurationJsonFromYaml(configuration)
                .Map(configurationJson.MergeWith)
                .IfNone(configurationJson);
    }

    private static Option<ConfigurationJson> GetConfigurationJsonFromYaml(IConfiguration configuration) =>
        configuration.TryGetValue("CONFIGURATION_YAML_PATH")
                     .Map(path => new FileInfo(path))
                     .Where(file => file.Exists)
                     .Map(file =>
                     {
                         using var reader = File.OpenText(file.FullName);
                         return ConfigurationJson.FromYaml(reader);
                     });
}