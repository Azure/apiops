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
using System;
using System.IO;
using System.Text.Json.Nodes;
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
        return Host.CreateDefaultBuilder(arguments)
                   .ConfigureAppConfiguration(ConfigureConfiguration)
                   .ConfigureServices(ConfigureServices);
    }

    private static void ConfigureConfiguration(IConfigurationBuilder builder)
    {
        // Add user secrets
        builder.AddUserSecrets(typeof(Program).Assembly);

        // Add YAML configuration
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
                .AddSingleton(GetHttpPipeline)
                .AddSingleton(GetDeleteRestResource)
                .AddSingleton(GetListRestResources)
                .AddSingleton(GetPutRestResource)
                .AddSingleton(GetPublisherParameters)
                .AddHostedService<Publisher>();
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

    private static HttpPipeline GetHttpPipeline(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var credential = GetTokenCredential(configuration);

        var armEnvironment = provider.GetRequiredService<GetArmEnvironment>()();
        var policy = new BearerTokenAuthenticationPolicy(credential, armEnvironment.DefaultScope);

        return HttpPipelineBuilder.Build(ClientOptions.Default, policy);
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

    private static DeleteRestResource GetDeleteRestResource(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DeleteRestResource));

        return async (uri, cancellationToken) =>
        {
            logger.LogDebug("Beginning request to delete REST resource at URI {uri}...", uri);
            await pipeline.DeleteResource(uri, cancellationToken);
            logger.LogDebug("Successfully deleted REST resource at URI {uri}.", uri);
        };
    }

    private static ListRestResources GetListRestResources(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ListRestResources));

        return (uri, cancellationToken) =>
        {
            logger.LogDebug("Listing REST resources at URI {uri}...", uri);
            return pipeline.ListJsonObjects(uri, cancellationToken);
        };
    }

    private static PutRestResource GetPutRestResource(IServiceProvider provider)
    {
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(PutRestResource));

        return async (uri, json, cancellationToken) =>
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Beginning request to put REST resource {json} at URI {uri}...", json.ToString(), uri);
            }
            else
            {
                logger.LogDebug("Beginning request to put REST resource URI {uri}...", uri);
            }

            await pipeline.PutResource(uri, json, cancellationToken);

            logger.LogDebug("Successfully put REST resource at URI {uri}.", uri);
        };
    }

    private static Publisher.Parameters GetPublisherParameters(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var armEnvironment = provider.GetRequiredService<GetArmEnvironment>()();

        return new Publisher.Parameters
        {
            ApplicationLifetime = provider.GetRequiredService<IHostApplicationLifetime>(),
            CommitId = TryGetCommitId(configuration),
            ConfigurationFile = TryGetConfigurationFile(configuration),
            ConfigurationJson = GetConfigurationJson(configuration),
            DeleteRestResource = provider.GetRequiredService<DeleteRestResource>(),
            ListRestResources = provider.GetRequiredService<ListRestResources>(),
            Logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Publisher)),
            PutRestResource = provider.GetRequiredService<PutRestResource>(),
            ServiceDirectory = GetServiceDirectory(configuration),
            ServiceUri = GetServiceUri(configuration, armEnvironment)
        };
    }

    private static CommitId? TryGetCommitId(IConfiguration configuration)
    {
        var commitId = configuration.TryGetValue("COMMIT_ID");

        return string.IsNullOrWhiteSpace(commitId)
                ? null
                : new CommitId(commitId);
    }

    private static FileInfo? TryGetConfigurationFile(IConfiguration configuration)
    {
        var filePath = configuration.TryGetValue("CONFIGURATION_YAML_PATH");

        return string.IsNullOrWhiteSpace(filePath)
                ? null
                : new FileInfo(filePath);
    }

    private static JsonObject GetConfigurationJson(IConfiguration configuration)
    {
        var configurationJson = SerializeConfiguration(configuration);

        return configurationJson is JsonObject jsonObject
                ? jsonObject
                : new JsonObject();
    }

    private static JsonNode? SerializeConfiguration(IConfiguration configuration)
    {
        var jsonObject = new JsonObject();

        foreach (var child in configuration.GetChildren())
        {
            if (child.Path.EndsWith(":0"))
            {
                var jsonArray = new JsonArray();

                foreach (var arrayChild in configuration.GetChildren())
                {
                    jsonArray.Add(SerializeConfiguration(arrayChild));
                }

                return jsonArray;
            }
            else
            {
                jsonObject.Add(child.Key, SerializeConfiguration(child));
            }
        }

        if (jsonObject.Count == 0 && configuration is IConfigurationSection configurationSection)
        {
            string? sectionValue = configurationSection.Value;

            if (bool.TryParse(sectionValue, out var boolValue))
            {
                return JsonValue.Create(boolValue);
            }
            else if (decimal.TryParse(sectionValue, out var decimalValue))
            {
                return JsonValue.Create(decimalValue);
            }
            else if (long.TryParse(sectionValue, out var longValue))
            {
                return JsonValue.Create(longValue);
            }
            else
            {
                return JsonValue.Create(sectionValue);
            }
        }
        else
        {
            return jsonObject;
        }
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

    private delegate ArmEnvironment GetArmEnvironment();
}
