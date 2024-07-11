using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using Flurl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using System;
using System.IO;
using System.Reflection;

namespace common;

public sealed record AzureEnvironment(Uri AuthorityHost, string DefaultScope, Uri ManagementEndpoint)
{
    public static AzureEnvironment Public { get; } = new(AzureAuthorityHosts.AzurePublicCloud, ArmEnvironment.AzurePublicCloud.DefaultScope, ArmEnvironment.AzurePublicCloud.Endpoint);

    public static AzureEnvironment USGovernment { get; } = new(AzureAuthorityHosts.AzureGovernment, ArmEnvironment.AzureGovernment.DefaultScope, ArmEnvironment.AzureGovernment.Endpoint);

    public static AzureEnvironment Germany { get; } = new(AzureAuthorityHosts.AzureGermany, ArmEnvironment.AzureGermany.DefaultScope, ArmEnvironment.AzureGermany.Endpoint);

    public static AzureEnvironment China { get; } = new(AzureAuthorityHosts.AzureChina, ArmEnvironment.AzureChina.DefaultScope, ArmEnvironment.AzureChina.Endpoint);
}

public sealed record SubscriptionId : NonEmptyString
{
    public SubscriptionId(string value) : base(value) { }
}

public sealed record ResourceGroupName : NonEmptyString
{
    public ResourceGroupName(string value) : base(value) { }
}

public static class AzureModule
{
    private static void ConfigureAzureEnvironment(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetAzureEnvironment);
    }

    private static AzureEnvironment GetAzureEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT")
                            .Map(value => value switch
                            {
                                "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => AzureEnvironment.Public,
                                "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => AzureEnvironment.China,
                                "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => AzureEnvironment.USGovernment,
                                "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => AzureEnvironment.Germany,
                                _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
                            })
                            .IfNone(() => AzureEnvironment.Public);
    }

    private static void ConfigureTokenCredential(IHostApplicationBuilder builder)
    {
        ConfigureAzureEnvironment(builder);

        builder.Services.TryAddSingleton(GetTokenCredential);
    }

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var environment = provider.GetRequiredService<AzureEnvironment>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.TryGetValue("AZURE_BEARER_TOKEN")
                            .Map(GetCredentialFromToken)
                            .IfNone(() => GetDefaultAzureCredential(environment.AuthorityHost));


        static TokenCredential GetCredentialFromToken(string token)
        {
            var jsonWebToken = new JsonWebToken(token);
            var expirationDate = new DateTimeOffset(jsonWebToken.ValidTo);
            var accessToken = new AccessToken(token, expirationDate);

            return DelegatedTokenCredential.Create((context, cancellationToken) => accessToken);
        }

        static DefaultAzureCredential GetDefaultAzureCredential(Uri azureAuthorityHost) =>
            new(new DefaultAzureCredentialOptions
            {
                AuthorityHost = azureAuthorityHost
            });
    }

    public static void ConfigureHttpPipeline(IHostApplicationBuilder builder)
    {
        ConfigureTokenCredential(builder);
        ConfigureAzureEnvironment(builder);

        builder.Services.TryAddSingleton(GetHttpPipeline);
    }

    private static HttpPipeline GetHttpPipeline(IServiceProvider provider)
    {
        var tokenCredential = provider.GetRequiredService<TokenCredential>();
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();

        var clientOptions = ClientOptions.Default;
        clientOptions.RetryPolicy = new CommonRetryPolicy();

        var bearerAuthenticationPolicy = new BearerTokenAuthenticationPolicy(tokenCredential, azureEnvironment.DefaultScope);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(HttpPipeline));
        var loggingPolicy = new ILoggerHttpPipelinePolicy(logger);

        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version("-1");
        var telemetryPolicy = new TelemetryPolicy(version);

        return HttpPipelineBuilder.Build(clientOptions, bearerAuthenticationPolicy, loggingPolicy, telemetryPolicy);
    }

    private static void ConfigureManagementServiceName(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetManagementServiceName);
    }

    private static ManagementServiceName GetManagementServiceName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var name = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME")
                                .IfNone(() => configuration.GetValue("apimServiceName"));

        return ManagementServiceName.From(name);
    }

    public static void ConfigureManagementServiceUri(IHostApplicationBuilder builder)
    {
        ConfigureManagementServiceProviderUri(builder);
        ConfigureManagementServiceName(builder);

        builder.Services.TryAddSingleton(GetManagementServiceUri);
    }

    private static ManagementServiceUri GetManagementServiceUri(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ManagementServiceProviderUri>();
        var serviceName = provider.GetRequiredService<ManagementServiceName>();

        var uri = serviceProviderUri.ToUri()
                                    .AppendPathSegment(serviceName)
                                    .ToUri();

        return ManagementServiceUri.From(uri);
    }

    public static void ConfigureManagementServiceProviderUri(IHostApplicationBuilder builder)
    {
        ConfigureAzureEnvironment(builder);
        ConfigureSubscriptionId(builder);
        ConfigureResourceGroupName(builder);

        builder.Services.TryAddSingleton(GetManagementServiceProviderUri);
    }

    private static ManagementServiceProviderUri GetManagementServiceProviderUri(IServiceProvider provider)
    {
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var subscriptionId = provider.GetRequiredService<SubscriptionId>();
        var resourceGroupName = provider.GetRequiredService<ResourceGroupName>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var apiVersion = configuration.TryGetValue("ARM_API_VERSION")
                                      .IfNone(() => "2023-09-01-preview");

        var uri = azureEnvironment.ManagementEndpoint
                                  .AppendPathSegment("subscriptions")
                                  .AppendPathSegment(subscriptionId)
                                  .AppendPathSegment("resourceGroups")
                                  .AppendPathSegment(resourceGroupName)
                                  .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                  .SetQueryParam("api-version", apiVersion)
                                  .ToUri();

        return ManagementServiceProviderUri.From(uri);
    }

    private static void ConfigureSubscriptionId(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetSubscriptionId);
    }

    private static SubscriptionId GetSubscriptionId(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var subscriptionId = configuration.GetValue("AZURE_SUBSCRIPTION_ID");

        return new SubscriptionId(subscriptionId);
    }

    private static void ConfigureResourceGroupName(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetResourceGroupName);
    }

    private static ResourceGroupName GetResourceGroupName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

        return new ResourceGroupName(resourceGroupName);
    }

    public static void ConfigureManagementServiceDirectory(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetManagementServiceDirectory);
    }

    private static ManagementServiceDirectory GetManagementServiceDirectory(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var directoryPath = configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH");
        var directory = new DirectoryInfo(directoryPath);

        return ManagementServiceDirectory.From(directory);
    }
}