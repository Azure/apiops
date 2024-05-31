using Azure.Core;
using Azure.Core.Pipeline;
using common;
using common.tests;
using Flurl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Polly;
using publisher;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate IAsyncEnumerable<ManagementServiceName> ListApimServiceNames(CancellationToken cancellationToken);

internal delegate ValueTask DeleteApimService(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask CreateApimService(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask EmptyApimService(ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ValueTask PutServiceModel(ServiceModel serviceModel, ManagementServiceName serviceName, CancellationToken cancellationToken);

internal delegate ManagementServiceUri GetManagementServiceUri(ManagementServiceName serviceName);

internal delegate ValueTask WriteServiceModelArtifacts(ServiceModel serviceModel, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

internal delegate ValueTask<ImmutableArray<CommitId>> WriteServiceModelCommits(IEnumerable<ServiceModel> serviceModels, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken);

file sealed record ManagementServiceProviderUri : ResourceUri
{
    private readonly Uri uri;

    public ManagementServiceProviderUri(Uri uri)
    {
        this.uri = uri;
    }

    protected override Uri Value => uri;
}

file sealed class ListApimServiceNamesHandler(ILogger<ListApimServiceNames> logger,
                                              ActivitySource activitySource,
                                              HttpPipeline pipeline,
                                              ManagementServiceProviderUri serviceProviderUri)
{
    public async IAsyncEnumerable<ManagementServiceName> Handle([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(ListApimServiceNames));

        logger.LogInformation("Listing APIM service names...");

        var serviceNames = pipeline.ListJsonObjects(serviceProviderUri.ToUri(), cancellationToken)
                                   .Choose(json => json.TryGetStringProperty("name").ToOption())
                                   .Select(ManagementServiceName.From);

        await foreach (var serviceName in serviceNames.WithCancellation(cancellationToken))
        {
            yield return serviceName;
        }
    }
}

file sealed class DeleteApimServiceHandler(ILogger<DeleteApimService> logger,
                                           ActivitySource activitySource,
                                           HttpPipeline pipeline,
                                           ManagementServiceProviderUri serviceProviderUri)
{
    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(DeleteApimService));

        logger.LogInformation("Deleting APIM service {ServiceName}...", serviceName);

        var uri = serviceProviderUri.ToUri().AppendPathSegment(serviceName.Value).ToUri();

        try
        {
            await pipeline.DeleteResource(uri, waitForCompletion: false, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict && exception.Message.Contains("ServiceLocked", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}

file sealed class CreateApimServiceHandler(ILogger<CreateApimService> logger,
                                          ActivitySource activitySource,
                                          HttpPipeline pipeline,
                                          ManagementServiceProviderUri serviceProviderUri,
                                          AzureLocation location)
{
    private static readonly ResiliencePipeline httpResiliencePipeline =
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

    private static readonly ResiliencePipeline<string> statusResiliencePipeline =
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

    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(CreateApimService));

        logger.LogInformation("Creating APIM service {ServiceName}...", serviceName);

        var uri = serviceProviderUri.ToUri().AppendPathSegment(serviceName.Value).ToUri();
        var body = BinaryData.FromObjectAsJson(new
        {
            location = location.Name,
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
    }
}

file sealed class EmptyApimServiceHandler(ILogger<EmptyApimService> logger,
                                          ActivitySource activitySource,
                                          DeleteAllSubscriptions deleteSubscriptions,
                                          DeleteAllApis deleteApis,
                                          DeleteAllGroups deleteGroups,
                                          DeleteAllProducts deleteProducts,
                                          DeleteAllServicePolicies deleteServicePolicies,
                                          DeleteAllPolicyFragments deletePolicyFragments,
                                          DeleteAllDiagnostics deleteDiagnostics,
                                          DeleteAllLoggers deleteLoggers,
                                          DeleteAllBackends deleteBackends,
                                          DeleteAllVersionSets deleteVersionSets,
                                          DeleteAllGateways deleteGateways,
                                          DeleteAllTags deleteTags,
                                          DeleteAllNamedValues deleteNamedValues)
{
    private static ResiliencePipeline resiliencePipeline =
        new ResiliencePipelineBuilder()
                .AddRetry(new()
                {
                    BackoffType = DelayBackoffType.Constant,
                    UseJitter = true,
                    MaxRetryAttempts = 3,
                    ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(exception => exception.StatusCode == HttpStatusCode.PreconditionFailed && exception.Message.Contains("Resource was modified since last retrieval", StringComparison.OrdinalIgnoreCase))
                })
                .Build();

    public async ValueTask Handle(ManagementServiceName serviceName, CancellationToken cancellationToken)
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
    }
}

file sealed class PutServiceModelHandler(ILogger<PutServiceModel> logger,
                                         ActivitySource activitySource,
                                         PutNamedValueModels putNamedValues,
                                         PutTagModels putTags,
                                         PutVersionSetModels putVersionSets,
                                         PutBackendModels putBackends,
                                         PutLoggerModels putLoggers,
                                         PutDiagnosticModels putDiagnostics,
                                         PutPolicyFragmentModels putPolicyFragments,
                                         PutServicePolicyModels putServicePolicies,
                                         PutGroupModels putGroups,
                                         PutProductModels putProducts,
                                         PutApiModels putApis)
{
    public async ValueTask Handle(ServiceModel serviceModel, ManagementServiceName serviceName, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PutServiceModel));

        logger.LogInformation("Putting service model in APIM service {ServiceName}...", serviceName);

        await putNamedValues(serviceModel.NamedValues, serviceName, cancellationToken);
        await putTags(serviceModel.Tags, serviceName, cancellationToken);
        await putVersionSets(serviceModel.VersionSets, serviceName, cancellationToken);
        await putBackends(serviceModel.Backends, serviceName, cancellationToken);
        await putLoggers(serviceModel.Loggers, serviceName, cancellationToken);
        await putDiagnostics(serviceModel.Diagnostics, serviceName, cancellationToken);
        await putPolicyFragments(serviceModel.PolicyFragments, serviceName, cancellationToken);
        await putServicePolicies(serviceModel.ServicePolicies, serviceName, cancellationToken);
        await putGroups(serviceModel.Groups, serviceName, cancellationToken);
        await putProducts(serviceModel.Products, serviceName, cancellationToken);
        await putApis(serviceModel.Apis, serviceName, cancellationToken);
    }
}

file sealed class GetManagementServiceUriHandler(ManagementServiceProviderUri serviceProviderUri)
{
    public ManagementServiceUri Handle(ManagementServiceName serviceName) =>
        ManagementServiceUri.From(serviceProviderUri.ToUri()
                                                    .AppendPathSegment(serviceName.Value)
                                                    .ToUri());
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

file sealed class WriteServiceModelCommitsHandler(ILogger<WriteServiceModelCommits> logger,
                                                  ActivitySource activitySource,
                                                  WriteServiceModelArtifacts writeServiceModelArtifacts)
{
    public async ValueTask<ImmutableArray<CommitId>> Handle(IEnumerable<ServiceModel> models, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(WriteServiceModelCommits));

        logger.LogInformation("Writing service model commits to {ServiceDirectory}...", serviceDirectory);

        var authorName = "apiops";
        var authorEmail = "apiops@apiops.com";
        var repositoryDirectory = serviceDirectory.ToDirectoryInfo().Parent!;
        Git.InitializeRepository(repositoryDirectory, commitMessage: "Initial commit", authorName, authorEmail, DateTimeOffset.UtcNow);

        var commitIds = ImmutableArray<CommitId>.Empty;
        await models.Map((index, model) => (index, model))
                    .Iter(async x =>
                    {
                        var (index, model) = x;
                        DeleteNonGitDirectories(serviceDirectory);
                        await writeServiceModelArtifacts(model, serviceDirectory, cancellationToken);
                        var commit = Git.CommitChanges(repositoryDirectory, commitMessage: $"Commit {index}", authorName, authorEmail, DateTimeOffset.UtcNow);
                        var commitId = new CommitId(commit.Sha);
                        ImmutableInterlocked.Update(ref commitIds, commitIds => commitIds.Add(commitId));
                    }, cancellationToken);

        return commitIds;
    }

    private static void DeleteNonGitDirectories(ManagementServiceDirectory serviceDirectory) =>
        serviceDirectory.ToDirectoryInfo()
                        .ListDirectories("*")
                        .Where(directory => directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) is false)
                        .Iter(directory => directory.ForceDelete());
}

internal static class ManagementServices
{
    public static void ConfigureListApimServiceNames(IServiceCollection services)
    {
        ConfigureManagementServiceProviderUri(services);

        services.TryAddSingleton<ListApimServiceNamesHandler>();
        services.TryAddSingleton<ListApimServiceNames>(provider => provider.GetRequiredService<ListApimServiceNamesHandler>().Handle);
    }

    private static void ConfigureManagementServiceProviderUri(IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var apiVersion = configuration.TryGetValue("ARM_API_VERSION")
                                          .IfNone(() => "2022-08-01");

            var azureEnvironment = provider.GetRequiredService<AzureEnvironment>();
            var subscriptionId = provider.GetRequiredService<GetSubscriptionId>().Invoke();
            var resourceGroupName = provider.GetRequiredService<GetResourceGroupName>().Invoke();
            var uri = azureEnvironment.ManagementEndpoint
                                      .AppendPathSegment("subscriptions")
                                      .AppendPathSegment(subscriptionId)
                                      .AppendPathSegment("resourceGroups")
                                      .AppendPathSegment(resourceGroupName)
                                      .AppendPathSegment("providers/Microsoft.ApiManagement/service")
                                      .SetQueryParam("api-version", apiVersion)
                                      .ToUri();

            return new ManagementServiceProviderUri(uri);
        });
    }

    public static void ConfigureDeleteApimService(IServiceCollection services)
    {
        ConfigureManagementServiceProviderUri(services);

        services.TryAddSingleton<DeleteApimServiceHandler>();
        services.TryAddSingleton<DeleteApimService>(provider => provider.GetRequiredService<DeleteApimServiceHandler>().Handle);
    }

    public static void ConfigureCreateApimService(IServiceCollection services)
    {
        ConfigureManagementServiceProviderUri(services);
        ConfigureAzureLocation(services);

        services.TryAddSingleton<CreateApimServiceHandler>();
        services.TryAddSingleton<CreateApimService>(provider => provider.GetRequiredService<CreateApimServiceHandler>().Handle);
    }

    private static void ConfigureAzureLocation(IServiceCollection services)
    {
        services.TryAddSingleton(typeof(AzureLocation), provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var locationName = configuration.TryGetValue("AZURE_LOCATION")
                                            .IfNone("westus");

            return new AzureLocation("westus");
        });
    }

    public static void ConfigureEmptyApimService(IServiceCollection services)
    {
        SubscriptionServices.ConfigureDeleteAllSubscriptions(services);
        ApiServices.ConfigureDeleteAllApis(services);
        GroupServices.ConfigureDeleteAllGroups(services);
        ProductServices.ConfigureDeleteAllProducts(services);
        ServicePolicyServices.ConfigureDeleteAllServicePolicies(services);
        PolicyFragmentServices.ConfigureDeleteAllPolicyFragments(services);
        DiagnosticServices.ConfigureDeleteAllDiagnostics(services);
        LoggerServices.ConfigureDeleteAllLoggers(services);
        BackendServices.ConfigureDeleteAllBackends(services);
        VersionSetServices.ConfigureDeleteAllVersionSets(services);
        GatewayServices.ConfigureDeleteAllGateways(services);
        TagServices.ConfigureDeleteAllTags(services);
        NamedValueServices.ConfigureDeleteAllNamedValues(services);

        services.TryAddSingleton<EmptyApimServiceHandler>();
        services.TryAddSingleton<EmptyApimService>(provider => provider.GetRequiredService<EmptyApimServiceHandler>().Handle);
    }

    public static void ConfigurePutServiceModel(IServiceCollection services)
    {
        NamedValueServices.ConfigurePutNamedValueModels(services);
        TagServices.ConfigurePutTagModels(services);
        VersionSetServices.ConfigurePutVersionSetModels(services);
        BackendServices.ConfigurePutBackendModels(services);
        ApiServices.ConfigurePutApiModels(services);
        LoggerServices.ConfigurePutLoggerModels(services);
        DiagnosticServices.ConfigurePutDiagnosticModels(services);
        PolicyFragmentServices.ConfigurePutPolicyFragmentModels(services);
        ServicePolicyServices.ConfigurePutServicePolicyModels(services);
        GroupServices.ConfigurePutGroupModels(services);
        ProductServices.ConfigurePutProductModels(services);
        ApiServices.ConfigurePutApiModels(services);

        services.TryAddSingleton<PutServiceModelHandler>();
        services.TryAddSingleton<PutServiceModel>(provider => provider.GetRequiredService<PutServiceModelHandler>().Handle);
    }

    public static void ConfigureGetManagementServiceUri(IServiceCollection services)
    {
        ConfigureManagementServiceProviderUri(services);

        services.TryAddSingleton<GetManagementServiceUriHandler>();
        services.TryAddSingleton<GetManagementServiceUri>(provider => provider.GetRequiredService<GetManagementServiceUriHandler>().Handle);
    }

    public static void ConfigureWriteServiceModelArtifacts(IServiceCollection services)
    {
        NamedValueServices.ConfigureWriteNamedValueModels(services);
        TagServices.ConfigureWriteTagModels(services);
        VersionSetServices.ConfigureWriteVersionSetModels(services);
        BackendServices.ConfigureWriteBackendModels(services);
        LoggerServices.ConfigureWriteLoggerModels(services);
        DiagnosticServices.ConfigureWriteDiagnosticModels(services);
        PolicyFragmentServices.ConfigureWritePolicyFragmentModels(services);
        ServicePolicyServices.ConfigureWriteServicePolicyModels(services);
        GroupServices.ConfigureWriteGroupModels(services);
        ProductServices.ConfigureWriteProductModels(services);
        ApiServices.ConfigureWriteApiModels(services);

        services.TryAddSingleton<WriteServiceModelArtifactsHandler>();
        services.TryAddSingleton<WriteServiceModelArtifacts>(provider => provider.GetRequiredService<WriteServiceModelArtifactsHandler>().Handle);
    }

    public static void ConfigureWriteServiceModelCommits(IServiceCollection services)
    {
        ConfigureWriteServiceModelArtifacts(services);

        services.TryAddSingleton<WriteServiceModelCommitsHandler>();
        services.TryAddSingleton<WriteServiceModelCommits>(provider => provider.GetRequiredService<WriteServiceModelCommitsHandler>().Handle);
    }
}