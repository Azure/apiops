using Azure.Core;
using Azure.Core.Pipeline;
using common;
using common.tests;
using Flurl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

public delegate IAsyncEnumerable<ManagementServiceName> ListApimServiceNames(CancellationToken cancellationToken);
public delegate ValueTask DeleteApimService(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask CreateApimService(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ValueTask EmptyApimService(ManagementServiceName serviceName, CancellationToken cancellationToken);
public delegate ManagementServiceUri GetManagementServiceUri(ManagementServiceName serviceName);

public static class ManagementServiceModule
{
    public static void ConfigureListApimServiceNames(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceProviderUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetListApimServiceNames);
    }

    private static ListApimServiceNames GetListApimServiceNames(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ManagementServiceProviderUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(ListApimServiceNames));

            logger.LogInformation("Listing APIM service names...");

            return pipeline.ListJsonObjects(serviceProviderUri.ToUri(), cancellationToken)
                           .Choose(json => json.TryGetStringProperty("name").ToOption())
                           .Select(ManagementServiceName.From);
        };
    }

    public static void ConfigureDeleteApimService(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceProviderUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetDeleteApimService);
    }

    private static DeleteApimService GetDeleteApimService(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ManagementServiceProviderUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(DeleteApimService));

            logger.LogInformation("Deleting APIM service {ServiceName}...", serviceName);

            var uri = serviceProviderUri.ToUri()
                                        .AppendPathSegment(serviceName.Value)
                                        .ToUri();

            try
            {
                await pipeline.DeleteResource(uri, waitForCompletion: false, cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict && exception.Message.Contains("ServiceLocked", StringComparison.OrdinalIgnoreCase))
            {
            }
        };
    }

    public static void ConfigureCreateApimService(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceProviderUri(builder);
        AzureModule.ConfigureHttpPipeline(builder);

        builder.Services.TryAddSingleton(GetCreateApimService);
    }

    private static CreateApimService GetCreateApimService(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ManagementServiceProviderUri>();
        var pipeline = provider.GetRequiredService<HttpPipeline>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var location = configuration.TryGetValue("AZURE_LOCATION")
                                    .IfNone("westus");

        var httpResiliencePipeline =
            new ResiliencePipelineBuilder()
                .AddRetry(new()
                {
                    ShouldHandle = async arguments =>
                    {
                        await ValueTask.CompletedTask;

                        return arguments.Outcome.Exception?.Message?.Contains("is transitioning at this time", StringComparison.OrdinalIgnoreCase) ?? false;
                    },
                    Delay = TimeSpan.FromSeconds(5),
                    BackoffType = DelayBackoffType.Linear,
                    MaxRetryAttempts = 100
                })
                .AddTimeout(TimeSpan.FromMinutes(3))
                .Build();

        var statusResiliencePipeline =
            new ResiliencePipelineBuilder<string>()
                .AddRetry(new()
                {
                    ShouldHandle = async arguments =>
                    {
                        await ValueTask.CompletedTask;

                        if (arguments.Outcome.Exception?.Message?.Contains("is transitioning at this time", StringComparison.OrdinalIgnoreCase) ?? false)
                        {
                            return true;
                        }

                        var result = arguments.Outcome.Result;
                        var succeeded = "Succeeded".Equals(result, StringComparison.OrdinalIgnoreCase);
                        return succeeded is false;
                    },
                    Delay = TimeSpan.FromSeconds(5),
                    BackoffType = DelayBackoffType.Linear,
                    MaxRetryAttempts = 100
                })
                .AddTimeout(TimeSpan.FromMinutes(3))
                .Build();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(CreateApimService));

            logger.LogInformation("Creating APIM service {ServiceName}...", serviceName);

            var uri = serviceProviderUri.ToUri()
                                        .AppendPathSegment(serviceName.Value)
                                        .ToUri();

            var body = BinaryData.FromObjectAsJson(new
            {
                location = location,
                sku = new
                {
                    name = "StandardV2",
                    capacity = 1
                },
                identity = new
                {
                    type = "SystemAssigned"
                },
                properties = new
                {
                    publisherEmail = "admin@contoso.com",
                    publisherName = "Contoso"
                }
            });

            await httpResiliencePipeline.ExecuteAsync(async cancellationToken => await pipeline.PutContent(uri, body, cancellationToken), cancellationToken);

            // Wait until the service is successfully provisioned
            await statusResiliencePipeline.ExecuteAsync(async cancellationToken =>
            {
                var content = await pipeline.GetJsonObject(uri, cancellationToken);

                return content.TryGetJsonObjectProperty("properties")
                              .Bind(properties => properties.TryGetStringProperty("provisioningState"))
                              .IfLeft(string.Empty);
            }, cancellationToken);
        };
    }

    public static void ConfigureEmptyApimService(IHostApplicationBuilder builder)
    {
        SubscriptionModule.ConfigureDeleteAllSubscriptions(builder);
        ApiModule.ConfigureDeleteAllApis(builder);
        GroupModule.ConfigureDeleteAllGroups(builder);
        ProductModule.ConfigureDeleteAllProducts(builder);
        ServicePolicyModule.ConfigureDeleteAllServicePolicies(builder);
        PolicyFragmentModule.ConfigureDeleteAllPolicyFragments(builder);
        DiagnosticModule.ConfigureDeleteAllDiagnostics(builder);
        LoggerModule.ConfigureDeleteAllLoggers(builder);
        BackendModule.ConfigureDeleteAllBackends(builder);
        VersionSetModule.ConfigureDeleteAllVersionSets(builder);
        GatewayModule.ConfigureDeleteAllGateways(builder);
        TagModule.ConfigureDeleteAllTags(builder);
        NamedValueModule.ConfigureDeleteAllNamedValues(builder);

        builder.Services.TryAddSingleton(GetEmptyApimService);
    }

    private static EmptyApimService GetEmptyApimService(IServiceProvider provider)
    {
        var deleteSubscriptions = provider.GetRequiredService<DeleteAllSubscriptions>();
        var deleteApis = provider.GetRequiredService<DeleteAllApis>();
        var deleteGroups = provider.GetRequiredService<DeleteAllGroups>();
        var deleteProducts = provider.GetRequiredService<DeleteAllProducts>();
        var deleteServicePolicies = provider.GetRequiredService<DeleteAllServicePolicies>();
        var deletePolicyFragments = provider.GetRequiredService<DeleteAllPolicyFragments>();
        var deleteDiagnostics = provider.GetRequiredService<DeleteAllDiagnostics>();
        var deleteLoggers = provider.GetRequiredService<DeleteAllLoggers>();
        var deleteBackends = provider.GetRequiredService<DeleteAllBackends>();
        var deleteVersionSets = provider.GetRequiredService<DeleteAllVersionSets>();
        var deleteGateways = provider.GetRequiredService<DeleteAllGateways>();
        var deleteTags = provider.GetRequiredService<DeleteAllTags>();
        var deleteNamedValues = provider.GetRequiredService<DeleteAllNamedValues>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var resiliencePipeline =
            new ResiliencePipelineBuilder()
                    .AddRetry(new()
                    {
                        BackoffType = DelayBackoffType.Constant,
                        UseJitter = true,
                        MaxRetryAttempts = 3,
                        ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(exception => exception.StatusCode == HttpStatusCode.PreconditionFailed && exception.Message.Contains("Resource was modified since last retrieval", StringComparison.OrdinalIgnoreCase))
                    })
                    .Build();

        return async (serviceName, cancellationToken) =>
        {
            using var _ = activitySource.StartActivity(nameof(EmptyApimService));

            logger.LogInformation("Emptying APIM service {ServiceName}...", serviceName);

            await resiliencePipeline.ExecuteAsync(async cancellationToken =>
            {
                await deleteSubscriptions(serviceName, cancellationToken);
                await deleteApis(serviceName, cancellationToken);
                await deleteGroups(serviceName, cancellationToken);
                await deleteProducts(serviceName, cancellationToken);
                await deleteServicePolicies(serviceName, cancellationToken);
                await deletePolicyFragments(serviceName, cancellationToken);
                await deleteDiagnostics(serviceName, cancellationToken);
                await deleteLoggers(serviceName, cancellationToken);
                await deleteBackends(serviceName, cancellationToken);
                await deleteVersionSets(serviceName, cancellationToken);
                await deleteGateways(serviceName, cancellationToken);
                await deleteTags(serviceName, cancellationToken);
                await deleteNamedValues(serviceName, cancellationToken);
            }, cancellationToken);
        };
    }

    public static void ConfigureGetManagementServiceUri(IHostApplicationBuilder builder)
    {
        AzureModule.ConfigureManagementServiceProviderUri(builder);

        builder.Services.TryAddSingleton(GetManagementServiceUri);
    }

    private static GetManagementServiceUri GetManagementServiceUri(IServiceProvider provider)
    {
        var serviceProviderUri = provider.GetRequiredService<ManagementServiceProviderUri>();

        return serviceName => ManagementServiceUri.From(serviceProviderUri.ToUri()
                                                                          .AppendPathSegment(serviceName.Value)
                                                                          .ToUri());
    }
}

file sealed class WriteServiceModelArtifactsHandler(ILogger<WriteServiceModelArtifacts> logger,
                                                    ActivitySource activitySource,
                                                    WriteNamedValueModels writeNamedValues,
                                                    WriteTagModels writeTags,
                                                    WriteVersionSetModels writeVersionSets,
                                                    WriteBackendModels writeBackends,
                                                    WriteLoggerModels writeLoggers,
                                                    WriteDiagnosticModels writeDiagnostics,
                                                    WritePolicyFragmentModels writePolicyFragments,
                                                    WriteServicePolicyModels writeServicePolicies,
                                                    WriteGroupModels writeGroups,
                                                    WriteProductModels writeProducts,
                                                    WriteApiModels writeApis)
{
    public async ValueTask Handle(ServiceModel serviceModel, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteServiceModelArtifacts));

        logger.LogInformation("Writing service model artifacts to {ServiceDirectory}...", serviceDirectory);

        await writeNamedValues(serviceModel.NamedValues, serviceDirectory, cancellationToken);
        await writeTags(serviceModel.Tags, serviceDirectory, cancellationToken);
        await writeVersionSets(serviceModel.VersionSets, serviceDirectory, cancellationToken);
        await writeBackends(serviceModel.Backends, serviceDirectory, cancellationToken);
        await writeLoggers(serviceModel.Loggers, serviceDirectory, cancellationToken);
        await writeDiagnostics(serviceModel.Diagnostics, serviceDirectory, cancellationToken);
        await writePolicyFragments(serviceModel.PolicyFragments, serviceDirectory, cancellationToken);
        await writeServicePolicies(serviceModel.ServicePolicies, serviceDirectory, cancellationToken);
        await writeGroups(serviceModel.Groups, serviceDirectory, cancellationToken);
        await writeProducts(serviceModel.Products, serviceDirectory, cancellationToken);
        await writeApis(serviceModel.Apis, serviceDirectory, cancellationToken);
    }
}