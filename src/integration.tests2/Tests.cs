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
        var generateNextTestState = provider.GetRequiredService<GenerateNextTestState>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async cancellationToken =>
        {
            using var activity = activitySource.StartActivity("run.tests");

            var gen = from testState in generateTestState()
                      from serviceDirectory in generateServiceDirectory()
                      from extractorFilter in ExtractorFilter.Generate(testState.Models)
                      where extractorFilter.Resources.SelectMany(kvp => kvp.Value).SelectMany(kvp => kvp.Value).Any()
                      from publisherOverride in PublisherOverride.Generate(testState.Models)
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
            [TagResource.Instance] = typeof(TagModel),
            [NamedValueResource.Instance] = typeof(NamedValueModel),
            [LoggerResource.Instance] = typeof(LoggerModel),
            [DiagnosticResource.Instance] = typeof(DiagnosticModel),
            [ProductResource.Instance] = typeof(ProductModel)
        }.ToImmutableDictionary();

    public static ImmutableHashSet<IResource> Resources { get; } =
        [.. ResourceModels.Keys];
}