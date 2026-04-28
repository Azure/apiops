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
using System.Reflection;

namespace common;

public sealed record SubscriptionId
{
    private readonly string value;

    private SubscriptionId(string value)
    {
        this.value = value;
    }

    public static Result<SubscriptionId> From(string value) =>
        Guid.TryParse(value, out var _)
            ? new SubscriptionId(value)
            : Error.From($"Subscription ID '{value}' must be a valid GUID.");

    public override string ToString() => value;
}

public sealed record ResourceGroupName
{
    private readonly string value;

    private ResourceGroupName(string value)
    {
        this.value = value;
    }

    public static Result<ResourceGroupName> From(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.From($"Resource group name '{value}' cannot be empty or whitespace.")
            : new ResourceGroupName(value);

    public override string ToString() => value;
}

public sealed record ServiceProviderUri
{
    private readonly Uri value;

    private ServiceProviderUri(Uri value)
    {
        this.value = value;
    }

    public static Result<ServiceProviderUri> From(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? new ServiceProviderUri(uri)
            : Error.From($"'{value}' is not a valid URI.");

    public override string ToString() => value.ToString();

    public Uri ToUri() =>
        value;
}

public sealed record AzureEnvironment(Uri AuthorityHost, string DefaultScope, Uri ManagementEndpoint)
{
    public static AzureEnvironment Public { get; } = new(AzureAuthorityHosts.AzurePublicCloud, ArmEnvironment.AzurePublicCloud.DefaultScope, ArmEnvironment.AzurePublicCloud.Endpoint);

    public static AzureEnvironment USGovernment { get; } = new(AzureAuthorityHosts.AzureGovernment, ArmEnvironment.AzureGovernment.DefaultScope, ArmEnvironment.AzureGovernment.Endpoint);

    public static AzureEnvironment China { get; } = new(AzureAuthorityHosts.AzureChina, ArmEnvironment.AzureChina.DefaultScope, ArmEnvironment.AzureChina.Endpoint);
}

public static class AzureModule
{
    public static void ConfigureAzureEnvironment(IHostApplicationBuilder builder) =>
        builder.TryAddSingleton(ResolveAzureEnvironment);

    private static AzureEnvironment ResolveAzureEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.GetValue("AZURE_CLOUD_ENVIRONMENT")
                            .Map(value => value switch
                            {
                                "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => AzureEnvironment.Public,
                                "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => AzureEnvironment.China,
                                "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => AzureEnvironment.USGovernment,
                                _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}.")
                            })
                            .IfNone(() => AzureEnvironment.Public);
    }

    public static void ConfigureTokenCredential(IHostApplicationBuilder builder)
    {
        ConfigureAzureEnvironment(builder);

        builder.TryAddSingleton(ResolveTokenCredential);
    }

    private static TokenCredential ResolveTokenCredential(IServiceProvider provider)
    {
        var environment = provider.GetRequiredService<AzureEnvironment>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.GetValue("AZURE_BEARER_TOKEN")
                            .Map(getCredentialFromToken)
                            .IfNone(getDefaultAzureCredential);


        static TokenCredential getCredentialFromToken(string token)
        {
            var jsonWebToken = new JsonWebToken(token);
            var expirationDate = new DateTimeOffset(jsonWebToken.ValidTo);
            var accessToken = new AccessToken(token, expirationDate);

            return DelegatedTokenCredential.Create((context, cancellationToken) => accessToken);
        }

        DefaultAzureCredential getDefaultAzureCredential() =>
            new(new DefaultAzureCredentialOptions
            {
                AuthorityHost = environment.AuthorityHost,
                ExcludeVisualStudioCredential = true
            });
    }

    public static void ConfigureHttpPipeline(IHostApplicationBuilder builder)
    {
        ConfigureTokenCredential(builder);
        ConfigureAzureEnvironment(builder);

        builder.TryAddSingleton(ResolveHttpPipeline);
    }

    private static HttpPipeline ResolveHttpPipeline(IServiceProvider provider)
    {
        var tokenCredential = provider.GetRequiredService<TokenCredential>();
        var environment = provider.GetRequiredService<AzureEnvironment>();

        var clientOptions = ClientOptions.Default;
        clientOptions.RetryPolicy = new CommonRetryPolicy();

        var authenticationPolicy = new BearerTokenAuthenticationPolicy(tokenCredential, environment.DefaultScope);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(HttpPipeline));
        var loggingPolicy = new LoggingPolicy(logger);

        var version = Assembly.GetEntryAssembly()?.GetName()?.Version ?? new Version("0.0.0");
        var telemetryPolicy = new TelemetryPolicy(version);

        return HttpPipelineBuilder.Build(clientOptions, authenticationPolicy, loggingPolicy, telemetryPolicy);
    }

    public static void ConfigureServiceProviderUri(IHostApplicationBuilder builder)
    {
        ConfigureAzureEnvironment(builder);
        ConfigureSubscriptionId(builder);
        ConfigureResourceGroupName(builder);

        builder.Services.TryAddSingleton(ResolveServiceProviderUri);
    }

    private static ServiceProviderUri ResolveServiceProviderUri(IServiceProvider provider)
    {
        var environment = provider.GetRequiredService<AzureEnvironment>();
        var subscriptionId = provider.GetRequiredService<SubscriptionId>();
        var resourceGroupName = provider.GetRequiredService<ResourceGroupName>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var apiVersion = configuration.GetValue("ARM_API_VERSION")
                                      .IfNone(() => "2024-05-01");

        var uri = environment.ManagementEndpoint
                             .AppendPathSegment("subscriptions")
                             .AppendPathSegment(subscriptionId)
                             .AppendPathSegment("resourceGroups")
                             .AppendPathSegment(resourceGroupName)
                             .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                             .SetQueryParam("api-version", apiVersion)
                             .ToString();

        return ServiceProviderUri.From(uri)
                                 .IfErrorThrow();
    }

    private static void ConfigureSubscriptionId(IHostApplicationBuilder builder) =>
        builder.Services.TryAddSingleton(ResolveSubscriptionId);

    private static SubscriptionId ResolveSubscriptionId(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var subscriptionId = configuration.GetValueOrThrow("AZURE_SUBSCRIPTION_ID");

        return SubscriptionId.From(subscriptionId)
                             .IfErrorThrow();
    }

    private static void ConfigureResourceGroupName(IHostApplicationBuilder builder) =>
        builder.Services.TryAddSingleton(ResolveResourceGroupName);

    private static ResourceGroupName ResolveResourceGroupName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var resourceGroupName = configuration.GetValueOrThrow("AZURE_RESOURCE_GROUP_NAME");

        return ResourceGroupName.From(resourceGroupName)
                                .IfErrorThrow();
    }
}