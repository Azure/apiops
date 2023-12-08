using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ApiManagement;
using Azure.ResourceManager.ApiManagement.Models;
using Azure.ResourceManager.Resources;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Medallion.Shell;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace publisher.integration.tests;

[SetUpFixture]
public class Fixture
{
    private static readonly IServiceProvider serviceProvider = GetServiceProvider();

    private static IConfiguration Configuration => serviceProvider.GetRequiredService<IConfiguration>();

    public static ServiceDirectory ExtractorServiceDirectory { get; } = GetExtractorServiceDirectory(Configuration);
    public static ServiceDirectory PublisherServiceDirectory { get; } = GetPublisherServiceDirectory(Configuration);

    private static ServiceProvider GetServiceProvider()
    {
        return new ServiceCollection().AddSingleton(GetConfiguration())
                                      .AddSingleton(GetTokenCredential)
                                      .AddSingleton(GetArmClient)
                                      .BuildServiceProvider();

    }

    private static IConfiguration GetConfiguration()
    {
        return new ConfigurationBuilder().AddEnvironmentVariables()
                                         .AddUserSecrets(typeof(Fixture).Assembly)
                                         .AddInMemoryCollection(new Dictionary<string, string?>
                                         {
                                             ["EXTRACTOR_ARTIFACTS_PATH"] = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
                                         })
                                         .Build();
    }

    private static TokenCredential GetTokenCredential(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var authorityHost = GetAzureAuthorityHost(configuration);

        return configuration.GetSection("AZURE_BEARER_TOKEN").Value switch
        {
            null => GetDefaultAzureCredential(authorityHost),
            var token => GetCredentialFromToken(token)
        };
    }

    private static Uri GetAzureAuthorityHost(IConfiguration configuration)
    {
        return configuration.GetSection("AZURE_CLOUD_ENVIRONMENT").Value switch
        {
            null => AzureAuthorityHosts.AzurePublicCloud,
            "AzureGlobalCloud" or nameof(AzureAuthorityHosts.AzurePublicCloud) => AzureAuthorityHosts.AzurePublicCloud,
            "AzureChinaCloud" or nameof(AzureAuthorityHosts.AzureChina) => AzureAuthorityHosts.AzureChina,
            "AzureUSGovernment" or nameof(AzureAuthorityHosts.AzureGovernment) => AzureAuthorityHosts.AzureGovernment,
            "AzureGermanCloud" or nameof(AzureAuthorityHosts.AzureGermany) => AzureAuthorityHosts.AzureGermany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(AzureAuthorityHosts.AzurePublicCloud)}, {nameof(AzureAuthorityHosts.AzureChina)}, {nameof(AzureAuthorityHosts.AzureGovernment)}, {nameof(AzureAuthorityHosts.AzureGermany)}")
        };
    }

    private static DefaultAzureCredential GetDefaultAzureCredential(Uri azureAuthorityHost)
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

    private static ArmClient GetArmClient(IServiceProvider provider)
    {
        var credential = provider.GetRequiredService<TokenCredential>();
        return new ArmClient(credential);
    }

    private static ServiceDirectory GetExtractorServiceDirectory(IConfiguration configuration)
    {
        var path = configuration.GetValue("EXTRACTOR_ARTIFACTS_PATH");
        return new(new(path));
    }

    private static ServiceDirectory GetPublisherServiceDirectory(IConfiguration configuration)
    {
        var path = configuration.GetValue("PUBLISHER_ARTIFACTS_PATH");
        return new(new(path));
    }

    [OneTimeSetUp]
    public async Task InitializeAsync()
    {
        TestContext.WriteLine("Initializing tests...");
        var cancellationToken = CancellationToken.None;

        TestContext.WriteLine("Ensuring APIM service is created...");
        await GetOrCreateApiManagementService(serviceProvider, cancellationToken);

        TestContext.WriteLine("Running publisher...");
        await RunPublisher(serviceProvider, cancellationToken);

        TestContext.WriteLine("Running extractor...");
        await RunExtractor(serviceProvider, cancellationToken);

        TestContext.WriteLine("Tests initialized.");
    }

    private static async ValueTask<ApiManagementServiceResource> GetOrCreateApiManagementService(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var option = await TryGetApiManagementService(serviceProvider, cancellationToken);

        return await option.IfNoneAsync(async () => await CreateService(serviceProvider, cancellationToken));
    }

    private static async ValueTask<Option<ApiManagementServiceResource>> TryGetApiManagementService(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var services = await GetServices(serviceProvider, cancellationToken);
        var serviceName = GetServiceName(serviceProvider);
        bool serviceExists = await services.ExistsAsync(serviceName, cancellationToken);

        return serviceExists
                ? Option<ApiManagementServiceResource>.Some(await services.GetAsync(serviceName, cancellationToken))
                : Option<ApiManagementServiceResource>.None;
    }

    private static async ValueTask<ApiManagementServiceCollection> GetServices(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var resourceGroup = await GetResourceGroup(serviceProvider, cancellationToken);
        return resourceGroup.GetApiManagementServices();
    }

    private static async ValueTask<ResourceGroupResource> GetResourceGroup(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var client = serviceProvider.GetRequiredService<ArmClient>();
        var subscription = await client.GetDefaultSubscriptionAsync(cancellationToken);
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var resourceGroupName = configuration.GetValue("AZURE_RESOURCE_GROUP_NAME");

        return await subscription.GetResourceGroupAsync(resourceGroupName, cancellationToken);
    }

    private static string GetServiceName(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return configuration.GetValue("AZURE_API_MANAGEMENT_SERVICE_NAME");
    }

    private static async ValueTask<ApiManagementServiceResource> CreateService(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var resourceGroup = await GetResourceGroup(serviceProvider, cancellationToken);
        var services = resourceGroup.GetApiManagementServices();
        var serviceName = GetServiceName(serviceProvider);

        var location = resourceGroup.Data.Location;
        var sku = new ApiManagementServiceSkuProperties(ApiManagementServiceSkuType.Consumption, capacity: 0);
        var publisherEmail = "admin@apiops.com";
        var publisherName = "admin";
        var serviceData = new ApiManagementServiceData(location, sku, publisherEmail, publisherName);

        var operation = await services.CreateOrUpdateAsync(WaitUntil.Started, serviceName, serviceData, cancellationToken);
        return await operation.WaitForCompletionAsync(cancellationToken);
    }

    private static async ValueTask RunPublisher(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var service = await GetOrCreateApiManagementService(serviceProvider, cancellationToken);
        var serviceIdentifier = service.Id;

        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var command = Command.Run("dotnet",
                                  "run",
                                  "--project",
                                  $@"{configuration.GetValue("PUBLISHER_PROJECT_PATH")}",
                                  "--AZURE_BEARER_TOKEN",
                                  await GetBearerToken(serviceProvider, cancellationToken),
                                  "--API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH",
                                  $@"{configuration.GetValue("PUBLISHER_ARTIFACTS_PATH")}",
                                  "--API_MANAGEMENT_SERVICE_NAME",
                                  serviceIdentifier.Name,
                                  "--AZURE_SUBSCRIPTION_ID",
                                  serviceIdentifier.SubscriptionId ?? throw new InvalidOperationException("Subscription ID is null"),
                                  "--AZURE_RESOURCE_GROUP_NAME",
                                  serviceIdentifier.ResourceGroupName ?? throw new InvalidOperationException("Resource group name is null"));
        var commandResult = await command.Task;
        if (commandResult.Success is false)
        {
            throw new InvalidOperationException($"Running publisher failed with error {commandResult.StandardError}. Output is {commandResult.StandardOutput}");
        }
    }

    private static async ValueTask RunExtractor(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var service = await GetOrCreateApiManagementService(serviceProvider, cancellationToken);
        var serviceIdentifier = service.Id;

        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var command = Command.Run("dotnet",
                                  "run",
                                  "--project",
                                  $@"{configuration.GetValue("EXTRACTOR_PROJECT_PATH")}",
                                  "--AZURE_BEARER_TOKEN",
                                  await GetBearerToken(serviceProvider, cancellationToken),
                                  "--API_MANAGEMENT_SERVICE_OUTPUT_FOLDER_PATH",
                                  $@"{configuration.GetValue("EXTRACTOR_ARTIFACTS_PATH")}",
                                  "--API_MANAGEMENT_SERVICE_NAME",
                                  serviceIdentifier.Name,
                                  "--AZURE_SUBSCRIPTION_ID",
                                  serviceIdentifier.SubscriptionId ?? throw new InvalidOperationException("Subscription ID is null"),
                                  "--AZURE_RESOURCE_GROUP_NAME",
                                  serviceIdentifier.ResourceGroupName ?? throw new InvalidOperationException("Resource group name is null"));
        var commandResult = await command.Task;
        if (commandResult.Success is false)
        {
            throw new InvalidOperationException($"Running extractor failed with error {commandResult.StandardError}. Output is {commandResult.StandardOutput}");
        }
    }

    private static async ValueTask<string> GetBearerToken(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var tokenCredential = serviceProvider.GetRequiredService<TokenCredential>();

        var environment = GetArmEnvironment(serviceProvider);
        var requestContext = new TokenRequestContext(scopes: new[] { environment.Endpoint.ToString() });

        var token = await tokenCredential.GetTokenAsync(requestContext, cancellationToken);
        return token.Token;
    }

    private static ArmEnvironment GetArmEnvironment(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();

        return configuration.TryGetValue("AZURE_CLOUD_ENVIRONMENT").ValueUnsafe() switch
        {
            null => ArmEnvironment.AzurePublicCloud,
            "AzureGlobalCloud" or nameof(ArmEnvironment.AzurePublicCloud) => ArmEnvironment.AzurePublicCloud,
            "AzureChinaCloud" or nameof(ArmEnvironment.AzureChina) => ArmEnvironment.AzureChina,
            "AzureUSGovernment" or nameof(ArmEnvironment.AzureGovernment) => ArmEnvironment.AzureGovernment,
            "AzureGermanCloud" or nameof(ArmEnvironment.AzureGermany) => ArmEnvironment.AzureGermany,
            _ => throw new InvalidOperationException($"AZURE_CLOUD_ENVIRONMENT is invalid. Valid values are {nameof(ArmEnvironment.AzurePublicCloud)}, {nameof(ArmEnvironment.AzureChina)}, {nameof(ArmEnvironment.AzureGovernment)}, {nameof(ArmEnvironment.AzureGermany)}")
        };
    }

    [OneTimeTearDown]
    public async Task Cleanup()
    {
        await ValueTask.CompletedTask;
        TestContext.WriteLine("Cleaning up...");

        var cancellationToken = CancellationToken.None;
        var option = await TryGetApiManagementService(serviceProvider, cancellationToken);

        await option.IterAsync(async resource =>
        {
            TestContext.WriteLine("Deleting APIM resource...");
            await resource.DeleteAsync(WaitUntil.Completed, cancellationToken);
        });

        TestContext.WriteLine("Cleanup complete.");
    }
}
