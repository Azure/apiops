using Azure.Core.Pipeline;
using common;
using common.tests;
using Flurl;
using LanguageExt;
using Polly;
using publisher;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal static class ServiceModule
{
    public static async ValueTask DeleteManagementServices(Uri serviceProviderUri, string serviceNamesToDeletePrefix, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        try
        {
            await pipeline.ListJsonObjects(serviceProviderUri, cancellationToken)
                      .Choose(json => json.TryGetStringProperty("name").ToOption())
                      .Where(name => name.StartsWith(serviceNamesToDeletePrefix, StringComparison.OrdinalIgnoreCase))
                      .Select(name => ManagementServiceUri.From(serviceProviderUri.AppendPathSegment(name).ToUri()))
                      .IterParallel(async uri => await DeleteManagementService(uri, pipeline, cancellationToken), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return;
        }
    }

    public static async ValueTask DeleteManagementService(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await pipeline.DeleteResource(serviceUri.ToUri(), waitForCompletion: true, cancellationToken);

    public static async ValueTask CreateManagementService(ServiceModel model, ManagementServiceUri serviceUri, string location, HttpPipeline pipeline, CancellationToken cancellationToken)
    {
        var body = BinaryData.FromObjectAsJson(new
        {
            location = location,
            sku = new
            {
                name = "BasicV2",
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

        await pipeline.PutContent(serviceUri.ToUri(), body, cancellationToken);

        // Wait until the service is successfully provisioned
        var resiliencePipeline = GetCreationStatusResiliencePipeline();
        await resiliencePipeline.ExecuteAsync(async cancellationToken =>
        {
            var content = await pipeline.GetJsonObject(serviceUri.ToUri(), cancellationToken);

            return content.TryGetJsonObjectProperty("properties")
                          .Bind(properties => properties.TryGetStringProperty("provisioningState"))
                          .IfLeft(string.Empty);
        }, cancellationToken);
    }

    private static ResiliencePipeline<string> GetCreationStatusResiliencePipeline() =>
        new ResiliencePipelineBuilder<string>()
            .AddRetry(new()
            {
                ShouldHandle = async arguments =>
                {
                    await ValueTask.CompletedTask;
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

    public static async ValueTask Put(ServiceModel model, ManagementServiceUri serviceUri, HttpPipeline pipeline, bool putSpecialSkuResources, CancellationToken cancellationToken)
    {
        await NamedValue.Put(model.NamedValues, serviceUri, pipeline, cancellationToken);
        await Tag.Put(model.Tags, serviceUri, pipeline, cancellationToken);
        await VersionSet.Put(model.VersionSets, serviceUri, pipeline, cancellationToken);
        await Backend.Put(model.Backends, serviceUri, pipeline, cancellationToken);
        await Logger.Put(model.Loggers, serviceUri, pipeline, cancellationToken);
        await Diagnostic.Put(model.Diagnostics, serviceUri, pipeline, cancellationToken);
        await PolicyFragment.Put(model.PolicyFragments, serviceUri, pipeline, cancellationToken);
        await Group.Put(model.Groups, serviceUri, pipeline, cancellationToken);
        await Api.Put(model.Apis, serviceUri, pipeline, cancellationToken);
        await ServicePolicy.Put(model.ServicePolicies, serviceUri, pipeline, cancellationToken);
        await Product.Put(model.Products, serviceUri, pipeline, cancellationToken);

        if (putSpecialSkuResources)
        {
            await Gateway.Put(model.Gateways, serviceUri, pipeline, cancellationToken);
        }

        await Subscription.Put(model.Subscriptions, serviceUri, pipeline, cancellationToken);
    }

    public static async ValueTask DeleteAll(ManagementServiceUri serviceUri, HttpPipeline pipeline, CancellationToken cancellationToken) =>
        await ResiliencePipelines.DeletePolicy.ExecuteAsync(async cancellationToken =>
        {
            await Subscription.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Api.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Group.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Product.DeleteAll(serviceUri, pipeline, cancellationToken);
            await ServicePolicy.DeleteAll(serviceUri, pipeline, cancellationToken);
            await PolicyFragment.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Diagnostic.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Logger.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Backend.DeleteAll(serviceUri, pipeline, cancellationToken);
            await VersionSet.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Gateway.DeleteAll(serviceUri, pipeline, cancellationToken);
            await Tag.DeleteAll(serviceUri, pipeline, cancellationToken);
            await NamedValue.DeleteAll(serviceUri, pipeline, cancellationToken);
        }, cancellationToken);

    public static void DeleteServiceDirectory(ManagementServiceDirectory serviceDirectory)
    {
        var directoryInfo = serviceDirectory.ToDirectoryInfo();

        if (directoryInfo.Exists())
        {
            directoryInfo.ForceDelete();
        }
    }

    public static async ValueTask ValidateExtractedArtifacts(ExtractorOptions extractorOptions, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, bool validateSpecialSkuResources, CancellationToken cancellationToken)
    {
        await NamedValue.ValidateExtractedArtifacts(extractorOptions.NamedValueNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Tag.ValidateExtractedArtifacts(extractorOptions.TagNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await VersionSet.ValidateExtractedArtifacts(extractorOptions.VersionSetNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Backend.ValidateExtractedArtifacts(extractorOptions.BackendNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Logger.ValidateExtractedArtifacts(extractorOptions.LoggerNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Diagnostic.ValidateExtractedArtifacts(extractorOptions.DiagnosticNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await PolicyFragment.ValidateExtractedArtifacts(extractorOptions.PolicyFragmentNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await ServicePolicy.ValidateExtractedArtifacts(serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Product.ValidateExtractedArtifacts(extractorOptions.ProductNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Group.ValidateExtractedArtifacts(extractorOptions.GroupNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Api.ValidateExtractedArtifacts(extractorOptions.ApiNamesToExport, extractorOptions.DefaultApiSpecification, serviceDirectory, serviceUri, pipeline, cancellationToken);
        await Subscription.ValidateExtractedArtifacts(extractorOptions.SubscriptionNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);

        if (validateSpecialSkuResources)
        {
            await Gateway.ValidateExtractedArtifacts(extractorOptions.GatewayNamesToExport, serviceDirectory, serviceUri, pipeline, cancellationToken);
        }
    }

    public static async ValueTask WriteArtifacts(ServiceModel model, ManagementServiceDirectory serviceDirectory, bool writeSpecialSkuResources, CancellationToken cancellationToken)
    {
        await NamedValue.WriteArtifacts(model.NamedValues, serviceDirectory, cancellationToken);
        await Tag.WriteArtifacts(model.Tags, serviceDirectory, cancellationToken);
        await Gateway.WriteArtifacts(model.Gateways, serviceDirectory, cancellationToken);
        await VersionSet.WriteArtifacts(model.VersionSets, serviceDirectory, cancellationToken);
        await Backend.WriteArtifacts(model.Backends, serviceDirectory, cancellationToken);
        await Logger.WriteArtifacts(model.Loggers, serviceDirectory, cancellationToken);
        await Diagnostic.WriteArtifacts(model.Diagnostics, serviceDirectory, cancellationToken);
        await PolicyFragment.WriteArtifacts(model.PolicyFragments, serviceDirectory, cancellationToken);
        await ServicePolicy.WriteArtifacts(model.ServicePolicies, serviceDirectory, cancellationToken);
        await Product.WriteArtifacts(model.Products, serviceDirectory, cancellationToken);
        await Group.WriteArtifacts(model.Groups, serviceDirectory, cancellationToken);
        await Api.WriteArtifacts(model.Apis, serviceDirectory, cancellationToken);
        await Subscription.WriteArtifacts(model.Subscriptions, serviceDirectory, cancellationToken);
    }

    public static async ValueTask ValidatePublisherChanges(PublisherOptions publisherOptions, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, bool validateSpecialSkuResources, CancellationToken cancellationToken)
    {
        await NamedValue.ValidatePublisherChanges(serviceDirectory, publisherOptions.NamedValueOverrides, serviceUri, pipeline, cancellationToken);
        await Tag.ValidatePublisherChanges(serviceDirectory, publisherOptions.TagOverrides, serviceUri, pipeline, cancellationToken);
        await VersionSet.ValidatePublisherChanges(serviceDirectory, publisherOptions.VersionSetOverrides, serviceUri, pipeline, cancellationToken);
        await Backend.ValidatePublisherChanges(serviceDirectory, publisherOptions.BackendOverrides, serviceUri, pipeline, cancellationToken);
        await Logger.ValidatePublisherChanges(serviceDirectory, publisherOptions.LoggerOverrides, serviceUri, pipeline, cancellationToken);
        await Diagnostic.ValidatePublisherChanges(serviceDirectory, publisherOptions.DiagnosticOverrides, serviceUri, pipeline, cancellationToken);
        await PolicyFragment.ValidatePublisherChanges(serviceDirectory, publisherOptions.PolicyFragmentOverrides, serviceUri, pipeline, cancellationToken);
        await ServicePolicy.ValidatePublisherChanges(serviceDirectory, publisherOptions.ServicePolicyOverrides, serviceUri, pipeline, cancellationToken);
        await Product.ValidatePublisherChanges(serviceDirectory, publisherOptions.ProductOverrides, serviceUri, pipeline, cancellationToken);
        await Group.ValidatePublisherChanges(serviceDirectory, publisherOptions.GroupOverrides, serviceUri, pipeline, cancellationToken);
        await Api.ValidatePublisherChanges(serviceDirectory, publisherOptions.ApiOverrides, serviceUri, pipeline, cancellationToken);
        await Subscription.ValidatePublisherChanges(serviceDirectory, publisherOptions.SubscriptionOverrides, serviceUri, pipeline, cancellationToken);

        if (validateSpecialSkuResources)
        {
            await Gateway.ValidatePublisherChanges(serviceDirectory, publisherOptions.GatewayOverrides, serviceUri, pipeline, cancellationToken);
        }
    }

    public static async ValueTask<ImmutableArray<CommitId>> WriteCommitArtifacts(IEnumerable<ServiceModel> models, ManagementServiceDirectory serviceDirectory, bool writeSpecialSkuResources, CancellationToken cancellationToken)
    {
        var authorName = "apiops";
        var authorEmail = "apiops@apiops.com";
        var serviceDirectoryInfo = serviceDirectory.ToDirectoryInfo();
        Git.InitializeRepository(serviceDirectoryInfo, commitMessage: "Initial commit", authorName, authorEmail, DateTimeOffset.UtcNow);

        var commitIds = ImmutableArray<CommitId>.Empty;
        await models.Map((index, model) => (index, model))
                    .Iter(async x =>
                    {
                        var (index, model) = x;
                        DeleteNonGitDirectories(serviceDirectory);
                        await WriteArtifacts(model, serviceDirectory, writeSpecialSkuResources, cancellationToken);
                        var commit = Git.CommitChanges(serviceDirectoryInfo, commitMessage: $"Commit {index}", authorName, authorEmail, DateTimeOffset.UtcNow);
                        var commitId = new CommitId(commit.Sha);
                        ImmutableInterlocked.Update(ref commitIds, commitIds => commitIds.Add(commitId));
                    }, cancellationToken);

        return commitIds;
    }

    private static void DeleteNonGitDirectories(ManagementServiceDirectory serviceDirectory) =>
        serviceDirectory.ToDirectoryInfo()
                        .ListDirectories("*")
                        .Where(directory => directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) is false)
                        .Iter(directory => directory.Delete(recursive: true));

    public static async ValueTask ValidatePublisherCommitChanges(PublisherOptions publisherOptions, CommitId commitId, ManagementServiceDirectory serviceDirectory, ManagementServiceUri serviceUri, HttpPipeline pipeline, bool validateSpecialSkuResources, CancellationToken cancellationToken)
    {
        await NamedValue.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.NamedValueOverrides, serviceUri, pipeline, cancellationToken);
        await Tag.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.TagOverrides, serviceUri, pipeline, cancellationToken);
        await VersionSet.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.VersionSetOverrides, serviceUri, pipeline, cancellationToken);
        await Backend.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.BackendOverrides, serviceUri, pipeline, cancellationToken);
        await Logger.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.LoggerOverrides, serviceUri, pipeline, cancellationToken);
        await Diagnostic.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.DiagnosticOverrides, serviceUri, pipeline, cancellationToken);
        await PolicyFragment.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.PolicyFragmentOverrides, serviceUri, pipeline, cancellationToken);
        await ServicePolicy.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.ServicePolicyOverrides, serviceUri, pipeline, cancellationToken);
        await Product.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.ProductOverrides, serviceUri, pipeline, cancellationToken);
        await Group.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.GroupOverrides, serviceUri, pipeline, cancellationToken);
        await Api.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.ApiOverrides, serviceUri, pipeline, cancellationToken);
        await Subscription.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.SubscriptionOverrides, serviceUri, pipeline, cancellationToken);

        if (validateSpecialSkuResources)
        {
            await Gateway.ValidatePublisherCommitChanges(commitId, serviceDirectory, publisherOptions.GatewayOverrides, serviceUri, pipeline, cancellationToken);
        }
    }
}

file static class ResiliencePipelines
{
    private static readonly Lazy<ResiliencePipeline> deletePolicy = new(() =>
    new ResiliencePipelineBuilder()
                .AddRetry(new()
                {
                    BackoffType = DelayBackoffType.Constant,
                    UseJitter = true,
                    MaxRetryAttempts = 3,
                    ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(exception => exception.StatusCode == HttpStatusCode.PreconditionFailed && exception.Message.Contains("Resource was modified since last retrieval", StringComparison.OrdinalIgnoreCase))
                })
                .Build());

    public static ResiliencePipeline DeletePolicy => deletePolicy.Value;
}