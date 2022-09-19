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
        services.AddSingleton(GetExtractorParameters)
                .AddHostedService<Extractor>();
    }

    private static Extractor.Parameters GetExtractorParameters(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var armEnvironment = GetArmEnvironment(configuration);
        var authenticatedPipeline = GetAuthenticatedHttpPipeline(configuration, armEnvironment);

        return new Extractor.Parameters
        {
            ApiNamesToExport = GetApiNamesToExport(configuration),
            ApiSpecification = GetApiSpecification(configuration),
            ApplicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>(),
            DownloadResource = GetDownloadResource(),
            GetRestResource = GetGetRestResource(authenticatedPipeline),
            ListRestResources = GetListRestResources(authenticatedPipeline),
            Logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Extractor)),
            ServiceDirectory = GetServiceDirectory(configuration),
            ServiceUri = GetServiceUri(configuration, armEnvironment)
        };
    }

    private static ArmEnvironment GetArmEnvironment(IConfiguration configuration)
    {
        return configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT") switch
        {
            null => ArmEnvironment.AzurePublicCloud,
            "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => ArmEnvironment.AzurePublicCloud,
            "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => ArmEnvironment.AzureChina,
            "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => ArmEnvironment.AzureGovernment,
            "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => ArmEnvironment.AzureGermany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
        };
    }

    private static IEnumerable<string>? GetApiNamesToExport(IConfiguration configuration)
    {
        return configuration.TryGetSection("apiNames")
                           ?.Get<IEnumerable<string>>();
    }

    private static OpenApiSpecification GetApiSpecification(IConfiguration configuration)
    {
        var configurationFormat = configuration.TryGetValue("API_SPECIFICATION_FORMAT")
                                  ?? configuration.TryGetValue("apiSpecificationFormat");

        return configurationFormat is null
            ? new OpenApiSpecification(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml)
            : configurationFormat switch
            {
                _ when configurationFormat.Equals("JSON", StringComparison.OrdinalIgnoreCase) => new OpenApiSpecification(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Json),
                _ when configurationFormat.Equals("YAML", StringComparison.OrdinalIgnoreCase) => new OpenApiSpecification(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml),
                _ when configurationFormat.Equals("OpenApiV2Json", StringComparison.OrdinalIgnoreCase) => new OpenApiSpecification(OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Json),
                _ when configurationFormat.Equals("OpenApiV2Yaml", StringComparison.OrdinalIgnoreCase) => new OpenApiSpecification(OpenApiSpecVersion.OpenApi2_0, OpenApiFormat.Yaml),
                _ when configurationFormat.Equals("OpenApiV3Json", StringComparison.OrdinalIgnoreCase) => new OpenApiSpecification(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Json),
                _ when configurationFormat.Equals("OpenApiV3Yaml", StringComparison.OrdinalIgnoreCase) => new OpenApiSpecification(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml),
                _ => throw new InvalidOperationException($"API specification format '{configurationFormat}' defined in configuration is not supported.")
            };
    }

    private static DownloadResource GetDownloadResource()
    {
        var pipeline = HttpPipelineBuilder.Build(ClientOptions.Default);

        return async (uri, cancellationToken) =>
        {
            var content = await pipeline.GetContent(uri, cancellationToken);
            return content.ToStream();
        };
    }

    private static AuthenticatedHttpPipeline GetAuthenticatedHttpPipeline(IConfiguration configuration, ArmEnvironment armEnvironment)
    {
        var credential = GetTokenCredential(configuration);
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

    private static GetRestResource GetGetRestResource(AuthenticatedHttpPipeline authenticatedHttpPipeline)
    {
        return async (uri, cancellationToken) => await authenticatedHttpPipeline.Pipeline.GetJsonObject(uri, cancellationToken);
    }

    private static ListRestResources GetListRestResources(AuthenticatedHttpPipeline authenticatedHttpPipeline)
    {
        return (uri, cancellationToken) => authenticatedHttpPipeline.Pipeline.ListJsonObjects(uri, cancellationToken);
    }

    private static ServiceDirectory GetServiceDirectory(IConfiguration configuration)
    {
        var directoryPath = configuration.GetValue("API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH");
        var directory = new DirectoryInfo(directoryPath);
        return new ServiceDirectory(directory);
    }

    private static ServiceUri GetServiceUri(IConfiguration configuration, ArmEnvironment armEnvironment)
    {
        var uri = armEnvironment.Endpoint.AppendPathSegment("subscriptions")
                                         .AppendPathSegment(configuration.GetValue("AZURE_SUBSCRIPTION_ID"))
                                         .AppendPathSegment("resourceGroups")
                                         .AppendPathSegment(configuration.GetValue("AZURE_RESOURCE_GROUP_NAME"))
                                         .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                         .AppendPathSegment(configuration.GetValue("API_MANAGEMENT_SERVICE_NAME"))
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
}
