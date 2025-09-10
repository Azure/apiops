using common;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask RunIntegrationTests(CancellationToken cancellationToken);

internal static class IntegrationTestsModule
{
    public static void ConfigureRunIntegrationTests(IHostApplicationBuilder builder)
    {
        ServiceModule.ConfigureEmptyService(builder);
        ServiceModule.ConfigurePopulateService(builder);
        ResourceGraphModule.ConfigureBuilder(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
        ExtractorModule.ConfigureValidateExtractor(builder);
        PublisherModule.ConfigureRunPublisher(builder);
        PublisherModule.ConfigureValidatePublisherWithoutCommit(builder);
        PublisherModule.ConfigureValidatePublisherWithCommit(builder);
        FileSystemModule.ConfigureWriteGitCommits(builder);
        builder.TryAddSingleton(GetRunIntegrationTests);
    }

    private static RunIntegrationTests GetRunIntegrationTests(IServiceProvider provider)
    {
        var emptyService = provider.GetRequiredService<EmptyService>();
        var populateService = provider.GetRequiredService<PopulateService>();
        var graph = provider.GetRequiredService<ResourceGraph>();
        var runExtractor = provider.GetRequiredService<RunExtractor>();
        var validateExtractor = provider.GetRequiredService<ValidateExtractor>();
        var runPublisher = provider.GetRequiredService<RunPublisher>();
        var validatePublisherWithoutCommit = provider.GetRequiredService<ValidatePublisherWithoutCommit>();
        var validatePublisherWithCommit = provider.GetRequiredService<ValidatePublisherWithCommit>();
        var writeGitCommits = provider.GetRequiredService<WriteGitCommits>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger>();

        return async cancellationToken =>
        {
            using var activity = activitySource.StartActivity("run.integration.tests");

            //var seedOption = Option.Some("000dPf54ST01"); // Uncomment and update to replay a specific seed
            var seedOption = Option<string>.None();

            var gen = TestParameters.Generate(graph);
            await gen.SampleAsync(async parameters =>
            {
                logger.LogInformation("{Parameters}", parameters.Serialize().ToJsonString());

                await cleanUp(parameters, cancellationToken);

                logger.LogInformation("Testing extractor...");
                await testExtractor(parameters, cancellationToken);

                logger.LogInformation("Testing publisher with extracted artifacts...");
                await testPublisherWithExtractedArtifacts(parameters, cancellationToken);

                logger.LogInformation("Testing publisher with commits...");
                await testPublisherWithCommits(parameters, cancellationToken);

                logger.LogInformation("Cleaning up...");
                await cleanUp(parameters, cancellationToken);
            }, iter: 1, threads: 1, seed: seedOption.IfNoneNull());
        };

#pragma warning disable CS8321 // Local function is declared but never used
        async ValueTask testExtractor(TestParameters parameters, CancellationToken cancellationToken)
        {
            await cleanUp(parameters, cancellationToken);

            var extractorModels = parameters.ExtractorParameters.Models;
            await populateService(extractorModels, cancellationToken);

            var extractorSubset = parameters.ExtractorParameters.SubsetToExtract;
            var extractorOptions = new ExtractorOptions
            {
                ServiceDirectory = parameters.ServiceDirectory,
                Models = extractorSubset
            };
            await runExtractor(extractorOptions, cancellationToken);

            await validateExtractor(extractorModels, extractorSubset, parameters.ServiceDirectory, cancellationToken);
        }

        async ValueTask cleanUp(TestParameters parameters, CancellationToken cancellationToken)
        {
            var serviceDirectory = parameters.ServiceDirectory;
            serviceDirectory.ToDirectoryInfo().DeleteIfExists();
            await emptyService(cancellationToken);
        }

        async ValueTask testPublisherWithExtractedArtifacts(TestParameters parameters, CancellationToken cancellationToken)
        {
            await emptyService(cancellationToken);

            var extractorOverrides = parameters.PublisherParameters.ExtractorOverrides;
            var publisherOptions = new PublisherOptions
            {
                ServiceDirectory = parameters.ServiceDirectory,
                JsonOverrides = extractorOverrides
            };
            await runPublisher(publisherOptions, cancellationToken);

            await validatePublisherWithoutCommit(parameters.PublisherParameters.ExtractorModels, extractorOverrides, cancellationToken);
        }

        async ValueTask testPublisherWithCommits(TestParameters parameters, CancellationToken cancellationToken)
        {
            await cleanUp(parameters, cancellationToken);

            // Write Git commits
            var serviceDirectory = parameters.ServiceDirectory;
            var publisherParameters = parameters.PublisherParameters;
            var commitIds = await writeGitCommits(publisherParameters.ModelChain, serviceDirectory, cancellationToken);

            var testCount = publisherParameters.TestCount;
            var tests = from index in Enumerable.Range(0, testCount)
                        select new
                        {
                            CommitId = commitIds[index],
                            Models = publisherParameters.ModelChain[index],
                            Overrides = index == testCount - 1
                                            ? publisherParameters.LastTestOverrides
                                            : Option<JsonObject>.None(),
                            PreviousModels = index == 0
                                                ? Option<ResourceModels>.None()
                                                : publisherParameters.ModelChain[index - 1]
                        };

            await tests.IterTask(async test =>
            {
                var publisherOptions = new PublisherOptions
                {
                    ServiceDirectory = serviceDirectory,
                    CommitId = test.CommitId,
                    JsonOverrides = test.Overrides
                };

                await runPublisher(publisherOptions, cancellationToken);
                await validatePublisherWithCommit(test.Models, test.Overrides, test.PreviousModels, cancellationToken);
            }, cancellationToken);
        }
    }
#pragma warning restore CS8321 // Local function is declared but never used
}

file sealed record ExtractorParameters
{
    public required ResourceModels Models { get; init; }
    public Option<ResourceModels> SubsetToExtract { get; init; } = Option.None;

    public static Gen<ExtractorParameters> Generate(ResourceGraph graph) =>
        from models in Generator.GenerateResourceModels(graph)
        from subset in Generator.GenerateSubSetOf(models).OptionOf()
        select new ExtractorParameters
        {
            Models = models,
            SubsetToExtract = subset
        };

    public JsonObject Serialize() =>
        new()
        {
            ["models"] = Models.Serialize(),
            ["subsetToExtract"] = SubsetToExtract.Map(models => models.Serialize())
                                                 .IfNoneNull()
        };
}

file sealed record PublisherParameters
{
    public required ResourceModels ExtractorModels { get; init; }
    public Option<JsonObject> ExtractorOverrides { get; init; } = Option.None;
    public required int TestCount { get; init; }
    public required ImmutableArray<ResourceModels> ModelChain { get; init; }
    public required Option<JsonObject> LastTestOverrides { get; init; }

    public static Gen<PublisherParameters> Generate(ResourceModels extractorModels, ResourceGraph graph) =>
        from extractorOverrides in Generator.GeneratePublisherOverrides(extractorModels, graph).OptionOf()
        let testCount = 2
        from chainLength in Gen.Int[testCount, 10]
        from chain in Enumerable.Range(0, chainLength - 1)
                                .Aggregate(from models in Generator.GenerateResourceModels(graph)
                                           select ImmutableArray.Create(models),
                                           (previousGen, _) => from previous in previousGen
                                                               let last = previous.Last()
                                                               from updatedModel in Generator.GenerateUpdatedResourceModels(last, graph)
                                                               select previous.Add(updatedModel))
        from lastTestOverrides in Generator.GeneratePublisherOverrides(chain[testCount - 1], graph).OptionOf()
        select new PublisherParameters
        {
            ExtractorModels = extractorModels,
            ExtractorOverrides = extractorOverrides,
            TestCount = testCount,
            ModelChain = chain,
            LastTestOverrides = lastTestOverrides
        };

    public JsonObject Serialize() =>
        new()
        {
            ["extractorOverrides"] = ExtractorOverrides.IfNoneNull(),
            ["firstCommitModels"] = ModelChain[0].Serialize(),
            ["secondCommitModels"] = ModelChain[1].Serialize(),
            ["lastTestOverrides"] = LastTestOverrides.IfNoneNull()
        };
}

file sealed record TestParameters
{
    public required ServiceDirectory ServiceDirectory { get; init; }
    public required ExtractorParameters ExtractorParameters { get; init; }
    public required PublisherParameters PublisherParameters { get; init; }

    public static Gen<TestParameters> Generate(ResourceGraph graph) =>
        from serviceDirectory in Generator.ServiceDirectory
        from extractorParameters in ExtractorParameters.Generate(graph)
        let publisherExtractorParameters = extractorParameters.SubsetToExtract.IfNone(() => extractorParameters.Models)
        from publisherParameters in PublisherParameters.Generate(publisherExtractorParameters, graph)
        select new TestParameters
        {
            ServiceDirectory = serviceDirectory,
            ExtractorParameters = extractorParameters,
            PublisherParameters = publisherParameters
        };

    public JsonObject Serialize() =>
        new JsonObject
        {
            ["serviceDirectory"] = ServiceDirectory.ToDirectoryInfo().FullName,
            ["extractorParameters"] = ExtractorParameters.Serialize(),
            ["publisherParameters"] = PublisherParameters.Serialize()
        };
}