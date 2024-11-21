using Azure.ResourceManager;
using Azure.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using LanguageExt;
using Azure.Core.Pipeline;
using Azure.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace common;

public sealed record AzureEnvironment(Uri AuthorityHost, string DefaultScope, Uri ManagementEndpoint)
{
    public static AzureEnvironment Public { get; } = new(AzureAuthorityHosts.AzurePublicCloud, ArmEnvironment.AzurePublicCloud.DefaultScope, ArmEnvironment.AzurePublicCloud.Endpoint);

    public static AzureEnvironment USGovernment { get; } = new(AzureAuthorityHosts.AzureGovernment, ArmEnvironment.AzureGovernment.DefaultScope, ArmEnvironment.AzureGovernment.Endpoint);

    public static AzureEnvironment China { get; } = new(AzureAuthorityHosts.AzureChina, ArmEnvironment.AzureChina.DefaultScope, ArmEnvironment.AzureChina.Endpoint);
}

public sealed record SubscriptionId
{
    private readonly string value;

    private SubscriptionId(string value) =>
        this.value = value;

    public sealed override string ToString() =>
        value;

    public static Fin<SubscriptionId> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? Fin<SubscriptionId>.Fail($"{typeof(SubscriptionId)} cannot be null or whitespace.")
        : new SubscriptionId(value);

    public bool Equals(SubscriptionId? other) => value.Equals(other?.value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => value.GetHashCode(StringComparison.OrdinalIgnoreCase);
}

public sealed record ResourceGroupName
{
    private readonly string value;

    private ResourceGroupName(string value) =>
        this.value = value;

    public sealed override string ToString() =>
        value;

    public static Fin<ResourceGroupName> From(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? Fin<ResourceGroupName>.Fail($"{typeof(ResourceGroupName)} cannot be null or whitespace.")
        : new ResourceGroupName(value);

    public bool Equals(ResourceGroupName? other) => value.Equals(other?.value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => value.GetHashCode(StringComparison.OrdinalIgnoreCase);
}

public static class AzureModule
{
    public static Func<HttpRequestException, IAsyncEnumerable<T>> GetMethodNotAllowedInPricingTierHandler<T>() =>
        exception =>
        exception switch
        {
            { StatusCode: HttpStatusCode.BadRequest } when exception.Message.Contains("MethodNotAllowedInPricingTier", StringComparison.OrdinalIgnoreCase) => AsyncEnumerable.Empty<T>(),
            _ => throw exception
        };

    public static void ConfigureHttpPipeline(IHostApplicationBuilder builder)
    {
        ConfigureTokenCredential(builder);
        ConfigureAzureEnvironment(builder);

        builder.Services.TryAddSingleton(GetHttpPipeline);
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

        return configuration.GetValue("AZURE_BEARER_TOKEN")
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

    public static void ConfigureAzureEnvironment(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetAzureEnvironment);
    }

    private static AzureEnvironment GetAzureEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.GetValue("AZURE_CLOUD_ENVIRONMENT")
                            .Map(value => value switch
                            {
                                "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => AzureEnvironment.Public,
                                "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => AzureEnvironment.China,
                                "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => AzureEnvironment.USGovernment,
                                _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
                            })
                            .IfNone(() => AzureEnvironment.Public);
    }

    public static void ConfigureSubscriptionId(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetSubscriptionId);
    }

    private static SubscriptionId GetSubscriptionId(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var result = from subscriptionIdString in configuration.GetValueOrFail("AZURE_SUBSCRIPTION_ID")
                     from subscriptionId in SubscriptionId.From(subscriptionIdString)
                     select subscriptionId;

        return result.ThrowIfFail();
    }

    public static void ConfigureResourceGroupName(IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(GetResourceGroupName);
    }

    private static ResourceGroupName GetResourceGroupName(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var result = from resourceGroupString in configuration.GetValueOrFail("AZURE_RESOURCE_GROUP_NAME")
                     from resourceGroup in ResourceGroupName.From(resourceGroupString)
                     select resourceGroup;

        return result.ThrowIfFail();
    }
}
