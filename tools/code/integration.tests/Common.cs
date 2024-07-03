using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using common;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal delegate string GetSubscriptionId();
internal delegate string GetResourceGroupName();
internal delegate ValueTask<string> GetBearerToken(CancellationToken cancellationToken);

internal static class CommonServices
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton(GetActivitySource);
        services.AddSingleton(GetAzureEnvironment);
        services.AddSingleton(GetTokenCredential);
        services.AddSingleton(GetHttpPipeline);
        services.AddSingleton(GetSubscriptionId);
        services.AddSingleton(GetResourceGroupName);
        services.AddSingleton(GetBearerToken);
        OpenTelemetryServices.Configure(services);
    }

    private static ActivitySource GetActivitySource(IServiceProvider provider) =>
        new("ApiOps.Integration.Tests");

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

    private static GetSubscriptionId GetSubscriptionId(IServiceProvider provider) =>
        () => provider.GetRequiredService<IConfiguration>().GetValue("AZURE_SUBSCRIPTION_ID");

    private static GetResourceGroupName GetResourceGroupName(IServiceProvider provider) =>
        () => provider.GetRequiredService<IConfiguration>().GetValue("AZURE_RESOURCE_GROUP_NAME");

    private static GetBearerToken GetBearerToken(IServiceProvider provider)
    {
        var tokenCredential = provider.GetRequiredService<TokenCredential>();
        var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
        var context = new TokenRequestContext([azureEnvironment.DefaultScope]);

        return async cancellationToken =>
        {
            var token = await tokenCredential.GetTokenAsync(context, cancellationToken);
            return token.Token;
        };
    }
}