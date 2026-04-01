using common;
using CsCheck;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask RunTests(CancellationToken cancellationToken);

internal static class TestsModule
{
    public static void ConfigureRunTests(IHostApplicationBuilder builder)
    {
        TestStateModule.ConfigureGenerateTestState(builder);
        TestStateModule.ConfigureGenerateUpdatedSubsetOfTestState(builder);
        TestStateModule.ConfigureGenerateNextTestState(builder);
        ApimModule.ConfigureWipeApim(builder);
        ApimModule.ConfigurePopulateApim(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
        ExtractorModule.ConfigureValidateExtractor(builder);
        PublisherModule.ConfigureRunPublisher(builder);
        PublisherModule.ConfigureValidatePublisher(builder);
        PublisherModule.ConfigureWriteGitCommit(builder);
        PublisherModule.ConfigureValidatePublisherStateTransition(builder);

        builder.TryAddSingleton(ResolveRunTests);
    }

    private static RunTests ResolveRunTests(IServiceProvider provider)
    {
        var wipeApim = provider.GetRequiredService<WipeApim>();
        var populateApim = provider.GetRequiredService<PopulateApim>();
        var runExtractor = provider.GetRequiredService<RunExtractor>();
        var validateExtractor = provider.GetRequiredService<ValidateExtractor>();
        var runPublisher = provider.GetRequiredService<RunPublisher>();
        var validatePublisher = provider.GetRequiredService<ValidatePublisher>();
        var writeGitCommit = provider.GetRequiredService<WriteGitCommit>();
        var validateStateTransition = provider.GetRequiredService<ValidatePublisherStateTransition>();
        var generateTestState = provider.GetRequiredService<GenerateTestState>();
        var generateUpdatedSubsetOfTestState = provider.GetRequiredService<GenerateUpdatedSubsetOfTestState>();
        var generateNextTestState = provider.GetRequiredService<GenerateNextTestState>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async cancellationToken =>
        {
            using var activity = activitySource.StartActivity("run.tests");

            var gen = from testState in generateTestState()
                      from serviceDirectory in generateServiceDirectory()
                      from extractorFilter in generateExtractorFilter(testState)
                      from publisherOverride in generatePublisherOverride(testState)
                      from nextState in generateNextTestState(testState)
                      select (testState, serviceDirectory, extractorFilter, publisherOverride, nextState);

            var seed = configuration.GetValue("SEED")
                                    .IfNoneNull();

            await gen.SampleAsync(async tuple =>
            {
                var (testState, serviceDirectory, extractorFilter, publisherOverride, nextState) = tuple;

                // Test filtered extraction                
                await wipeApim(cancellationToken);
                await populateApim(testState, cancellationToken);
                await runExtractor(serviceDirectory, extractorFilter, cancellationToken);
                await validateExtractor(testState, serviceDirectory, extractorFilter, cancellationToken);
                cleanupDirectory(serviceDirectory);

                // Test unfiltered extraction
                await runExtractor(serviceDirectory, filterOption: Option.None, cancellationToken);
                await validateExtractor(testState, serviceDirectory, filterOption: Option.None, cancellationToken);

                // Test publishing extracted artifacts without overrides
                await wipeApim(cancellationToken);
                await runPublisher(serviceDirectory, overrideOption: Option.None, commitIdOption: Option.None, cancellationToken);
                await validatePublisher(testState, serviceDirectory, overrideOption: Option.None, cancellationToken);

                // Test publishing with overrides
                await runPublisher(serviceDirectory, publisherOverride, commitIdOption: Option.None, cancellationToken);
                await validatePublisher(testState, serviceDirectory, publisherOverride, cancellationToken);

                // Test commit-based publish
                await wipeApim(cancellationToken);
                await populateApim(testState, cancellationToken);
                var commitId = await setupGitCommit(serviceDirectory, testState, nextState, cancellationToken);
                await runPublisher(serviceDirectory, overrideOption: Option.None, commitId, cancellationToken);
                await validateStateTransition(testState, nextState, cancellationToken);

                // Final cleanup
                await wipeApim(cancellationToken);
                cleanupDirectory(serviceDirectory);
            }, seed: seed, iter: 1, threads: 1);

            return;

            static Gen<ServiceDirectory> generateServiceDirectory() =>
                from folderName in Gen.String.AlphaNumeric
                where folderName.Length > 8
                where folderName.Length < 15
                let path = Path.Combine(Path.GetTempPath(), "apiops-integration-tests", folderName)
                select ServiceDirectory.FromPath(path);

            static Gen<ExtractorFilter> generateExtractorFilter(TestState testState) =>
                from extractorFilter in ExtractorFilter.Generate(testState.Models)
                let filterResourceNames = from parentDictionary in extractorFilter.Resources
                                          from resourceDictionary in parentDictionary.Value
                                          from resourceNames in resourceDictionary.Value
                                          select resourceNames
                where filterResourceNames.Any()
                select extractorFilter;

            Gen<PublisherOverride> generatePublisherOverride(TestState testState) =>
                from updatedSubset in generateUpdatedSubsetOfTestState(testState)
                let modelDictionary = updatedSubset.Models.ToImmutableDictionary(model => model.Key)
                select new PublisherOverride
                {
                    Updates = modelDictionary
                };
        };

        static void cleanupDirectory(ServiceDirectory directory) =>
            directory.ToDirectoryInfo()
                     .DeleteIfExists();

        async ValueTask<CommitId> setupGitCommit(ServiceDirectory serviceDirectory, TestState initialState, TestState nextState, CancellationToken cancellationToken)
        {
            cleanupDirectory(serviceDirectory);

            // Write initial commit
            await writeGitCommit(serviceDirectory, initialState, cancellationToken);

            // Write next commit and capture its ID
            var targetCommitId = await writeGitCommit(serviceDirectory, nextState, cancellationToken);

            // Write initial commit again. This ensures that the publisher works even if the commit ID is not the latest one.
            await writeGitCommit(serviceDirectory, initialState, cancellationToken);

            return targetCommitId;
        }
    }

    public static ImmutableDictionary<IResource, Type> ResourceModels { get; } =
        new Dictionary<IResource, Type>
        {
            [ApiResource.Instance] = typeof(ApiModel),
            [ApiDiagnosticResource.Instance] = typeof(ApiDiagnosticModel),
            [ApiPolicyResource.Instance] = typeof(ApiPolicyModel),
            [ApiOperationPolicyResource.Instance] = typeof(ApiOperationPolicyModel),
            [SubscriptionResource.Instance] = typeof(SubscriptionModel),
            [TagResource.Instance] = typeof(TagModel),
            [NamedValueResource.Instance] = typeof(NamedValueModel),
            [LoggerResource.Instance] = typeof(LoggerModel),
            [DiagnosticResource.Instance] = typeof(DiagnosticModel),
            [ProductResource.Instance] = typeof(ProductModel),
            [ProductApiResource.Instance] = typeof(ProductApiModel),
            [TagApiResource.Instance] = typeof(TagApiModel),
            [TagProductResource.Instance] = typeof(TagProductModel),
            [ProductPolicyResource.Instance] = typeof(ProductPolicyModel),
            [GroupResource.Instance] = typeof(GroupModel),
            [ProductGroupResource.Instance] = typeof(ProductGroupModel),
            [VersionSetResource.Instance] = typeof(VersionSetModel),
            [BackendResource.Instance] = typeof(BackendModel),
            [GatewayResource.Instance] = typeof(GatewayModel),
            [ServicePolicyResource.Instance] = typeof(ServicePolicyModel),
            [PolicyFragmentResource.Instance] = typeof(PolicyFragmentModel)
        }.ToImmutableDictionary();

    public static ImmutableHashSet<IResource> Resources { get; } =
        [.. ResourceModels.Keys];
}