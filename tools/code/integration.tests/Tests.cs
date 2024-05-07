using common;
using common.tests;
using CsCheck;
using DotNext.Collections.Generic;
using FluentAssertions;
using LanguageExt;
using NUnit.Framework;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;


[TestFixture]
public sealed class Tests
{
    private const string serviceNamePrefix = "apiopsinttest-";

#pragma warning disable CA1802 // Use literals where appropriate
#pragma warning disable CA1805 // Do not initialize unnecessarily
    private static readonly bool useExistingInstance = false;
#pragma warning restore CA1805 // Do not initialize unnecessarily
#pragma warning restore CA1802 // Use literals where appropriate

    [Test]
    public async Task Runs_as_expected()
    {
        AssertionOptions.FormattingOptions.MaxLines = 10000;
        AssertionOptions.AssertEquivalencyUsing(options => options.ComparingRecordsByValue());

        var cancellationToken = CancellationToken.None;
        await OneTimeSetup(cancellationToken);

        await WriteProgress("Generating fixture...");
        var generator = from fixture in Fixture.Generate(serviceNamePrefix)
                            // Use configuration service name for special SKUs
                        select useExistingInstance
                                ? fixture with
                                {
                                    FirstServiceName = Configuration.FirstServiceName,
                                    SecondServiceName = Configuration.SecondServiceName
                                }
                                : fixture with
                                {
                                    ServiceModel = fixture.ServiceModel with
                                    {
                                        Gateways = FrozenSet<GatewayModel>.Empty
                                    },
                                    PublishAllChangesModel = fixture.PublishAllChangesModel with
                                    {
                                        Gateways = FrozenSet<GatewayModel>.Empty
                                    },
                                    CommitModels = fixture.CommitModels.Select(model => model with
                                    {
                                        Gateways = FrozenSet<GatewayModel>.Empty
                                    }).ToImmutableArray()
                                };

        await generator.SampleAsync(async fixture =>
        {
            // 1. Set up the management service
            await CreateManagementServices(fixture, cancellationToken);
            DeleteServiceDirectory(fixture);

            // 2. Run the extractor and validate its artifacts
            await PutServiceModel(fixture, cancellationToken);
            await RunExtractor(fixture, cancellationToken);
            await ValidateExtractorArtifacts(fixture, cancellationToken);

            // 3. Publish the extracted changes to the empty second service
            await PublishExtractedArtifactsToSecondService(fixture, cancellationToken);
            await ValidatePublishedExtractedArtifacts(fixture, cancellationToken);
            await CleanUpExtractorResources(fixture, cancellationToken);

            // 3. Make changes to the extracted artifacts, publish the changes, then validate
            await WriteFirstChange(fixture, cancellationToken);
            await PublishFirstChange(fixture, cancellationToken);
            await ValidatePublishedFirstChange(fixture, cancellationToken);

            // 4. Write commits, publish changes in a specific commit, then validate
            var commits = await WriteCommitArtifacts(fixture, cancellationToken);
            await TestCommits(fixture, commits, cancellationToken);

            await CleanUp(fixture, cancellationToken);
        }, iter: 1, seed: "0000KOIPe036", threads: useExistingInstance ? 1 : -1);
    }

    private static async ValueTask WriteProgress(string message) =>
        await TestContext.Progress.WriteLineAsync($"{DateTime.Now:O}: {message}");

    private static async ValueTask OneTimeSetup(CancellationToken cancellationToken)
    {
        var serviceProviderUri = Configuration.ManagementServiceProviderUri;
        var pipeline = Configuration.HttpPipeline;

        await WriteProgress("Deleting management services...");
        await ServiceModule.DeleteManagementServices(serviceProviderUri, serviceNamePrefix, pipeline, cancellationToken);
    }

    private static async ValueTask CreateManagementServices(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Creating management services...");

        var firstServiceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);
        var secondServiceUri = Configuration.GetManagementServiceUri(fixture.SecondServiceName);

        if (useExistingInstance)
        {
            await Task.WhenAll(ServiceModule.DeleteAll(firstServiceUri, Configuration.HttpPipeline, cancellationToken).AsTask(),
                               ServiceModule.DeleteAll(secondServiceUri, Configuration.HttpPipeline, cancellationToken).AsTask());
        }
        else
        {
            await Task.WhenAll(ServiceModule.CreateManagementService(firstServiceUri, Configuration.Location, Configuration.HttpPipeline, cancellationToken).AsTask(),
                               ServiceModule.CreateManagementService(secondServiceUri, Configuration.Location, Configuration.HttpPipeline, cancellationToken).AsTask());
        }
    }

    private static void DeleteServiceDirectory(Fixture fixture)
    {
        ServiceModule.DeleteServiceDirectory(fixture.ServiceDirectory);
    }

    private static async ValueTask PutServiceModel(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Putting service model...");

        var serviceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);
        await ServiceModule.Put(fixture.ServiceModel, serviceUri, Configuration.HttpPipeline, useExistingInstance, cancellationToken);
    }

    private static async ValueTask RunExtractor(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Running extractor...");

        var bearerToken = await Configuration.GetBearerToken(cancellationToken);
        await Extractor.Run(fixture.ExtractorOptions, fixture.FirstServiceName, fixture.ServiceDirectory, Configuration.SubscriptionId, Configuration.ResourceGroupName, bearerToken, cancellationToken);
    }

    private static async ValueTask ValidateExtractorArtifacts(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Validating extractor artifacts...");

        var serviceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);
        await ServiceModule.ValidateExtractedArtifacts(fixture.ExtractorOptions, fixture.ServiceDirectory, serviceUri, Configuration.HttpPipeline, useExistingInstance, cancellationToken);
    }

    private static async ValueTask PublishExtractedArtifactsToSecondService(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Extracting all artifacts from first instance...");
        ServiceModule.DeleteServiceDirectory(fixture.ServiceDirectory);

        var bearerToken = await Configuration.GetBearerToken(cancellationToken);
        var extractorOptions = fixture.ExtractorOptions with
        {
            ApiNamesToExport = Option<FrozenSet<ApiName>>.None,
            ProductNamesToExport = Option<FrozenSet<ProductName>>.None,
            GroupNamesToExport = Option<FrozenSet<GroupName>>.None,
            SubscriptionNamesToExport = Option<FrozenSet<SubscriptionName>>.None,
            BackendNamesToExport = Option<FrozenSet<BackendName>>.None,
            LoggerNamesToExport = Option<FrozenSet<LoggerName>>.None,
            DiagnosticNamesToExport = Option<FrozenSet<DiagnosticName>>.None,
            PolicyFragmentNamesToExport = Option<FrozenSet<PolicyFragmentName>>.None,
            GatewayNamesToExport = Option<FrozenSet<GatewayName>>.None,
            TagNamesToExport = Option<FrozenSet<TagName>>.None,
            VersionSetNamesToExport = Option<FrozenSet<VersionSetName>>.None,
            NamedValueNamesToExport = Option<FrozenSet<NamedValueName>>.None,
        };
        await Extractor.Run(extractorOptions, fixture.FirstServiceName, fixture.ServiceDirectory, Configuration.SubscriptionId, Configuration.ResourceGroupName, bearerToken, cancellationToken);

        await WriteProgress("Publishing extracted artifacts to second instance...");
        await Publisher.Run(fixture.PublisherOptions, fixture.SecondServiceName, fixture.ServiceDirectory, Configuration.SubscriptionId, Configuration.ResourceGroupName, bearerToken, commitId: Option<CommitId>.None, cancellationToken);
    }

    private static async ValueTask ValidatePublishedExtractedArtifacts(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Validating published extracted artifacts...");

        var serviceUri = Configuration.GetManagementServiceUri(fixture.SecondServiceName);
        await ServiceModule.ValidatePublisherChanges(fixture.PublisherOptions, fixture.ServiceDirectory, serviceUri, Configuration.HttpPipeline, useExistingInstance, cancellationToken);
    }

    private static async ValueTask CleanUpExtractorResources(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Cleaning up extractor resources...");

        ServiceModule.DeleteServiceDirectory(fixture.ServiceDirectory);

        var firstServiceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);
        var secondServiceUri = Configuration.GetManagementServiceUri(fixture.SecondServiceName);
        await Task.WhenAll(ServiceModule.DeleteAll(firstServiceUri, Configuration.HttpPipeline, cancellationToken).AsTask(),
                           useExistingInstance
                           ? ServiceModule.DeleteAll(secondServiceUri, Configuration.HttpPipeline, cancellationToken).AsTask()
                           : ServiceModule.DeleteManagementService(secondServiceUri, Configuration.HttpPipeline, cancellationToken).AsTask());
    }

    private static async ValueTask WriteFirstChange(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Writing first change...");

        var firstChange = fixture.PublishAllChangesModel;
        await ServiceModule.WriteArtifacts(firstChange, fixture.ServiceDirectory, useExistingInstance, cancellationToken);
    }

    private static async ValueTask PublishFirstChange(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Publishing first change...");

        var bearerToken = await Configuration.GetBearerToken(cancellationToken);
        await Publisher.Run(fixture.PublisherOptions, fixture.FirstServiceName, fixture.ServiceDirectory, Configuration.SubscriptionId, Configuration.ResourceGroupName, bearerToken, commitId: Option<CommitId>.None, cancellationToken);
    }

    private static async ValueTask ValidatePublishedFirstChange(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Validating published first change...");

        var serviceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);
        await ServiceModule.ValidatePublisherChanges(fixture.PublisherOptions, fixture.ServiceDirectory, serviceUri, Configuration.HttpPipeline, useExistingInstance, cancellationToken);
    }

    private static async ValueTask<ImmutableArray<CommitId>> WriteCommitArtifacts(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Writing commit artifacts...");

        return await ServiceModule.WriteCommitArtifacts(fixture.CommitModels, fixture.ServiceDirectory, useExistingInstance, cancellationToken);
    }

    private static async ValueTask TestCommits(Fixture fixture, ImmutableArray<CommitId> commits, CancellationToken cancellationToken) =>
        await commits.Take(1)
                        .ForEachAsync(async (commit, cancellationToken) =>
                        {
                            await PublishCommit(fixture, commit, cancellationToken);
                            await ValidatePublishedCommit(fixture, commit, cancellationToken);
                        }, cancellationToken);

    private static async ValueTask PublishCommit(Fixture fixture, CommitId commitId, CancellationToken cancellationToken)
    {
        await WriteProgress($"Publishing commit {commitId}...");

        var bearerToken = await Configuration.GetBearerToken(cancellationToken);
        await Publisher.Run(fixture.PublisherOptions, fixture.FirstServiceName, fixture.ServiceDirectory, Configuration.SubscriptionId, Configuration.ResourceGroupName, bearerToken, commitId, cancellationToken);
    }

    private static async ValueTask ValidatePublishedCommit(Fixture fixture, CommitId commitId, CancellationToken cancellationToken)
    {
        await WriteProgress($"Validating published commit {commitId}...");

        var serviceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);
        await ServiceModule.ValidatePublisherCommitChanges(fixture.PublisherOptions, commitId, fixture.ServiceDirectory, serviceUri, Configuration.HttpPipeline, useExistingInstance, cancellationToken);
    }

    private static async ValueTask CleanUp(Fixture fixture, CancellationToken cancellationToken)
    {
        await WriteProgress("Cleaning up...");

        var serviceUri = Configuration.GetManagementServiceUri(fixture.FirstServiceName);

        if (useExistingInstance)
        {
            await ServiceModule.DeleteAll(serviceUri, Configuration.HttpPipeline, cancellationToken);
        }
        else
        {
            await ServiceModule.DeleteManagementService(serviceUri, Configuration.HttpPipeline, cancellationToken);
        }

        ServiceModule.DeleteServiceDirectory(fixture.ServiceDirectory);
    }
}