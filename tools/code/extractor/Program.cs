using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using common;
using Flurl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.OpenApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace extractor;

public static class Program
{
    public static async Task Main(string[] arguments)
    {
        await CreateBuilder(arguments).Build()
                                      .RunAsync();
    }

    private static IHostBuilder CreateBuilder(string[] arguments)
    {
        return Host.CreateDefaultBuilder(arguments)
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
        services.AddSingleton(GetGetArmEnvironment)
                .AddSingleton(GetAuthenticatedHttpPipeline)
                .AddSingleton(GetGetRestResource)
                .AddSingleton(GetListRestResources)
                .AddSingleton(GetDownloadResource)
                .AddSingleton(GetExtractorParameters)
                .AddHostedService<Extractor>();
    }

    private static GetArmEnvironment GetGetArmEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        var environment = configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT") switch
        {
            null => ArmEnvironment.AzurePublicCloud,
            "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => ArmEnvironment.AzurePublicCloud,
            "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => ArmEnvironment.AzureChina,
            "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => ArmEnvironment.AzureGovernment,
            "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => ArmEnvironment.AzureGermany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
        };

        return () => environment;
    }

    private static AuthenticatedHttpPipeline GetAuthenticatedHttpPipeline(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var credential = GetTokenCredential(configuration);

        var armEnvironment = provider.GetRequiredService<GetArmEnvironment>()();
        var policy = new BearerTokenAuthenticationPolicy(credential, armEnvironment.DefaultScope);

        return new AuthenticatedHttpPipeline(policy);
    }

    private static TokenCredential GetTokenCredential(IConfiguration configuration)
    {
        var authorityHost = GetAzureAuthorityHost(configuration);

        var token = configuration.TryGetValue("AZURE_BEARER_TOKEN");
        return token is null
                ? GetDefaultAzureCredential(authorityHost)
                : GetCredentialFromToken(token);
    }

    private static Uri GetAzureAuthorityHost(IConfiguration configuration)
    {
        return configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT") switch
        {
            null => AzureAuthorityHosts.AzurePublicCloud,
            "AzureGlobalCloud" or nameof(AzureAuthorityHosts.AzurePublicCloud) => AzureAuthorityHosts.AzurePublicCloud,
            "AzureChinaCloud" or nameof(AzureAuthorityHosts.AzureChina) => AzureAuthorityHosts.AzureChina,
            "AzureUSGovernment" or nameof(AzureAuthorityHosts.AzureGovernment) => AzureAuthorityHosts.AzureGovernment,
            "AzureGermanCloud" or nameof(AzureAuthorityHosts.AzureGermany) => AzureAuthorityHosts.AzureGermany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(AzureAuthorityHosts.AzurePublicCloud)}, {nameof(AzureAuthorityHosts.AzureChina)}, {nameof(AzureAuthorityHosts.AzureGovernment)}, {nameof(AzureAuthorityHosts.AzureGermany)}")
        };
    }

    private static TokenCredential GetDefaultAzureCredential(Uri azureAuthorityHost)
    {
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            AuthorityHost = azureAuthorityHost
        });
    }

    private static TokenCredential GetCredentialFromToken(string token)
    {
        var jsonWebToken = new JsonWebToken(token);
        var expirationDate = new DateTimeOffset(jsonWebToken.ValidTo);
        var accessToken = new AccessToken(token, expirationDate);

        return DelegatedTokenCredential.Create((context, cancellationToken) => accessToken);
    }

    private static DownloadResource GetDownloadResource(IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DownloadResource));
        var pipeline = HttpPipelineBuilder.Build(ClientOptions.Default, new UnauthenticatedPipelinePolicy());

        return async (uri, cancellationToken) =>
        {
            logger.LogDebug("Beginning request to download resource at URI {uri}...", uri);
            var content = await pipeline.GetContent(uri, cancellationToken);
            logger.LogDebug("Successfully downloaded resource at URI {uri}.", uri);

            return content.ToStream();
        };
    }

    private static GetRestResource GetGetRestResource(IServiceProvider provider)
    {
        var authenticatedPipeline = provider.GetRequiredService<AuthenticatedHttpPipeline>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(GetRestResource));

        return async (uri, cancellationToken) =>
        {
            logger.LogDebug("Beginning request to get REST resource at URI {uri}...", uri);

            var json = await authenticatedPipeline.Pipeline.GetJsonObject(uri, cancellationToken);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Successfully retrieved REST resource {json} at URI {uri}.", json.ToJsonString(), uri);
            }
            else
            {
                logger.LogDebug("Successfully retrieved REST resource at URI {uri}.", uri);
            }

            return json;
        };
    }

    private static ListRestResources GetListRestResources(IServiceProvider provider)
    {
        var authenticatedPipeline = provider.GetRequiredService<AuthenticatedHttpPipeline>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ListRestResources));

        return (uri, cancellationToken) =>
        {
            logger.LogDebug("Listing REST resources at URI {uri}...", uri);
            return authenticatedPipeline.Pipeline.ListJsonObjects(uri, cancellationToken);
        };
    }

    private static Extractor.Parameters GetExtractorParameters(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var armEnvironment = provider.GetRequiredService<GetArmEnvironment>()();

        return new Extractor.Parameters
        {
            ApiNamesToExport = GetApiNamesToExport(configuration),
            DefaultApiSpecification = GetApiSpecification(configuration),
            ApplicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>(),
            DownloadResource = provider.GetRequiredService<DownloadResource>(),
            GetRestResource = provider.GetRequiredService<GetRestResource>(),
            ListRestResources = provider.GetRequiredService<ListRestResources>(),
            Logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Extractor)),
            ServiceDirectory = GetServiceDirectory(configuration),
            ServiceUri = GetServiceUri(configuration, armEnvironment)
        };
    }

    private static IEnumerable<string>? GetApiNamesToExport(IConfiguration configuration)
    {
        return configuration.TryGetSection("apiNames")
                           ?.Get<IEnumerable<string>>();
    }

    private static DefaultApiSpecification GetApiSpecification(IConfiguration configuration)
    {
        var configurationFormat = configuration.TryGetValue("API_SPECIFICATION_FORMAT")
                                  ?? configuration.TryGetValue("apiSpecificationFormat");

        return configurationFormat is null
            ? new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml) as DefaultApiSpecification
            : configurationFormat switch
            {
                _ when configurationFormat.Equals("Wadl", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.Wadl(),
                _ when configurationFormat.Equals("JSON", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Json),
                _ when configurationFormat.Equals("YAML", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml),
                _ when configurationFormat.Equals("OpenApiV2Json", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Json),
                _ when configurationFormat.Equals("OpenApiV2Yaml", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Yaml),
                _ when configurationFormat.Equals("OpenApiV3Json", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Json),
                _ when configurationFormat.Equals("OpenApiV3Yaml", StringComparison.OrdinalIgnoreCase) => new DefaultApiSpecification.OpenApi(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml),
                _ => throw new InvalidOperationException($"API specification format '{configurationFormat}' defined in configuration is not supported.")
            };
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration)
    {
        var directoryPath = configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH");
        var directory = new DirectoryInfo(directoryPath);
        return new ServiceDirectory(directory);
    }

    private static ServiceUri GetServiceUri(IConfiguration configuration, ArmEnvironment armEnvironment)
    {
        var serviceName = configuration.TryGetValue("API_MANAGEMENT_SERVICE_NAME") ?? configuration.GetValue("apimServiceName");

        var uri = armEnvironment.Endpoint.AppendPathSegment("subscriptions")
                                         .AppendPathSegment(configuration.GetValue("AZURE_SUBSCRIPTION_ID"))
                                         .AppendPathSegment("resourceGroups")
                                         .AppendPathSegment(configuration.GetValue("AZURE_RESOURCE_GROUP_NAME"))
                                         .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                         .AppendPathSegment(serviceName)
                                         .SetQueryParam("api-version", "2021-12-01-preview")
                                         .ToUri();

        return new ServiceUri(uri);
    }

    private record AuthenticatedHttpPipeline
    {
        public HttpPipeline Pipeline { get; }

        public AuthenticatedHttpPipeline(BearerTokenAuthenticationPolicy policy)
        {
            Pipeline = HttpPipelineBuilder.Build(ClientOptions.Default, policy);
        }
    };

    private class UnauthenticatedPipelinePolicy : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            RemoveAuthorizationHeader(message);
            Process(message, pipeline);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            RemoveAuthorizationHeader(message);
            await ProcessNextAsync(message, pipeline);
        }

        private static void RemoveAuthorizationHeader(HttpMessage message)
        {
            if (message.Request.Headers.TryGetValue(HttpHeader.Names.Authorization, out var _))
            {
                message.Request.Headers.Remove(HttpHeader.Names.Authorization);
            }
        }
    }

    private delegate ArmEnvironment GetArmEnvironment();
}
