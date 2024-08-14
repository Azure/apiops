using common;
using common.tests;
using CsCheck;
using DotNext.Collections.Generic;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

public delegate ValueTask TestExtractor(CancellationToken cancellationToken);
public delegate ValueTask TestExtractThenPublish(CancellationToken cancellationToken);
public delegate ValueTask TestPublisher(CancellationToken cancellationToken);
public delegate ValueTask CleanUpTests(CancellationToken cancellationToken);
public delegate ValueTask TestWorkspaces(CancellationToken cancellationToken);

public static class TestModule
{
    public static void ConfigureTestExtractor(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureCreateApimService(builder);
        ManagementServiceModule.ConfigureEmptyApimService(builder);
        ServiceModelModule.ConfigurePutServiceModel(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
        ExtractorModule.ConfigureValidateExtractorArtifacts(builder);
        ManagementServiceModule.ConfigureDeleteApimService(builder);

        builder.Services.TryAddSingleton(GetTestExtractor);
    }

    private static TestExtractor GetTestExtractor(IServiceProvider provider)
    {
        var createService = provider.GetRequiredService<CreateApimService>();
        var emptyService = provider.GetRequiredService<EmptyApimService>();
        var putServiceModel = provider.GetRequiredService<PutServiceModel>();
        var runExtractor = provider.GetRequiredService<RunExtractor>();
        var validateExtractor = provider.GetRequiredService<ValidateExtractorArtifacts>();
        var deleteApimService = provider.GetRequiredService<DeleteApimService>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(TestExtractor));

            logger.LogInformation("Testing extractor...");

            await generateFixture().SampleAsync(async fixture => await run(fixture.Model, fixture.Options, fixture.ServiceName, fixture.ServiceDirectory, cancellationToken),
                                                iter: 1);
        };

        Gen<(ServiceModel Model, ExtractorOptions Options, ManagementServiceName ServiceName, ManagementServiceDirectory ServiceDirectory)> generateFixture() =>
            from serviceModel in ServiceModel.Generate()
            from extractorOptions in ExtractorOptions.Generate(serviceModel)
            from serviceName in Common.GenerateManagementServiceName(configuration, configurationKey: "FIRST_API_MANAGEMENT_SERVICE_NAME")
            from serviceDirectory in Common.GenerateManagementServiceDirectory()
            select (serviceModel, extractorOptions, serviceName, serviceDirectory);

        async ValueTask run(ServiceModel model, ExtractorOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            await createService(serviceName, cancellationToken);
            await emptyService(serviceName, cancellationToken);
            await putServiceModel(model, serviceName, cancellationToken);
            await runExtractor(options, serviceName, serviceDirectory, cancellationToken);
            await validateExtractor(options, serviceName, serviceDirectory, cancellationToken);
            await cleanUp(serviceName, serviceDirectory, cancellationToken);
        }

        async ValueTask cleanUp(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            if (serviceName.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                await deleteApimService(serviceName, cancellationToken);
            }

            Common.DeleteServiceDirectory(serviceDirectory);
        }
    }

    public static void ConfigureTestExtractThenPublish(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureCreateApimService(builder);
        ServiceModelModule.ConfigurePutServiceModel(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
        ManagementServiceModule.ConfigureDeleteApimService(builder);
        PublisherModule.ConfigureRunPublisher(builder);
        PublisherModule.ConfigureValidatePublishedArtifacts(builder);

        builder.Services.TryAddSingleton(GetTestExtractThenPublish);
    }

    private static TestExtractThenPublish GetTestExtractThenPublish(IServiceProvider provider)
    {
        var createService = provider.GetRequiredService<CreateApimService>();
        var putServiceModel = provider.GetRequiredService<PutServiceModel>();
        var runExtractor = provider.GetRequiredService<RunExtractor>();
        var deleteApimService = provider.GetRequiredService<DeleteApimService>();
        var runPublisher = provider.GetRequiredService<RunPublisher>();
        var validatePublisher = provider.GetRequiredService<ValidatePublishedArtifacts>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(TestExtractThenPublish));

            logger.LogInformation("Testing extracting, then publishing to a fresh instance...");

            await generateFixture().SampleAsync(async fixture => await run(fixture.Model, fixture.Options, fixture.SourceServiceName, fixture.DestinationServiceName, fixture.ServiceDirectory, cancellationToken),
                                                iter: 1);
        };

        async ValueTask run(ServiceModel model, PublisherOptions options, ManagementServiceName sourceServiceName, ManagementServiceName destinationServiceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            await createExtractorArtifacts(model, sourceServiceName, serviceDirectory, cancellationToken);
            await publishToDestination(options, destinationServiceName, serviceDirectory, cancellationToken);
            await validatePublisher(options, Option<CommitId>.None, destinationServiceName, serviceDirectory, cancellationToken);
            await cleanUp(sourceServiceName, destinationServiceName, serviceDirectory, cancellationToken);
        }

        async ValueTask createExtractorArtifacts(ServiceModel serviceModel, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Creating extractor artifacts...");

            await createService(serviceName, cancellationToken);
            await putServiceModel(serviceModel, serviceName, cancellationToken);
            await runExtractor(ExtractorOptions.NoFilter, serviceName, serviceDirectory, cancellationToken);
            await deleteApimService(serviceName, cancellationToken);
        }

        async ValueTask publishToDestination(PublisherOptions publisherOptions, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            logger.LogInformation("Publishing artifacts to destination...");

            await createService(serviceName, cancellationToken);
            await runPublisher(publisherOptions, serviceName, serviceDirectory, Option<CommitId>.None, cancellationToken);
        }

        async ValueTask cleanUp(ManagementServiceName sourceServiceName, ManagementServiceName destinationServiceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            await new[] { sourceServiceName, destinationServiceName }
                    .Where(name => name.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
                    .IterParallel(deleteApimService.Invoke, cancellationToken);

            Common.DeleteServiceDirectory(serviceDirectory);
        }

        Gen<(ServiceModel Model, PublisherOptions Options, ManagementServiceName SourceServiceName, ManagementServiceName DestinationServiceName, ManagementServiceDirectory ServiceDirectory)> generateFixture() =>
            from serviceModel in ServiceModel.Generate()
            from publisherOptions in PublisherOptions.Generate(serviceModel)
            from sourceServiceName in Common.GenerateManagementServiceName(configuration, "FIRST_API_MANAGEMENT_SERVICE_NAME")
            from destinationServiceName in Common.GenerateManagementServiceName(configuration, "SECOND_API_MANAGEMENT_SERVICE_NAME")
            from serviceDirectory in Common.GenerateManagementServiceDirectory()
            select (serviceModel, publisherOptions, sourceServiceName, destinationServiceName, serviceDirectory);
    }

    public static void ConfigureTestPublisher(IHostApplicationBuilder builder)
    {
        ServiceModelModule.ConfigureWriteServiceModelArtifacts(builder);
        ManagementServiceModule.ConfigureCreateApimService(builder);
        ManagementServiceModule.ConfigureEmptyApimService(builder);
        ServiceModelModule.ConfigureWriteServiceModelCommits(builder);
        PublisherModule.ConfigureRunPublisher(builder);
        PublisherModule.ConfigureValidatePublishedArtifacts(builder);
        ManagementServiceModule.ConfigureDeleteApimService(builder);

        builder.Services.TryAddSingleton(GetTestPublisher);
    }

    private static TestPublisher GetTestPublisher(IServiceProvider provider)
    {
        var writeArtifacts = provider.GetRequiredService<WriteServiceModelArtifacts>();
        var createService = provider.GetRequiredService<CreateApimService>();
        var emptyService = provider.GetRequiredService<EmptyApimService>();
        var writeCommits = provider.GetRequiredService<WriteServiceModelCommits>();
        var runPublisher = provider.GetRequiredService<RunPublisher>();
        var validatePublisher = provider.GetRequiredService<ValidatePublishedArtifacts>();
        var deleteApimService = provider.GetRequiredService<DeleteApimService>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(TestPublisher));

            logger.LogInformation("Testing publisher...");

            await generateFixture().SampleAsync(async fixture => await run(fixture.Model, fixture.CommitModels, fixture.Options, fixture.ServiceName, fixture.ServiceDirectory, cancellationToken),
                                                iter: 1);
        };

        async ValueTask run(ServiceModel model, ImmutableArray<ServiceModel> commitModels, PublisherOptions options, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            await publishAllChangesAndValidate(options, model, serviceName, serviceDirectory, cancellationToken);
            await publishCommitsAndValidate(options, commitModels, serviceName, serviceDirectory, cancellationToken);
            await cleanUp(serviceName, serviceDirectory, cancellationToken);
        }

        async ValueTask publishAllChangesAndValidate(PublisherOptions publisherOptions, ServiceModel serviceModel, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            await writeArtifacts(serviceModel, serviceDirectory, cancellationToken);
            await createService(serviceName, cancellationToken);
            await emptyService(serviceName, cancellationToken);
            await runPublisher(publisherOptions, serviceName, serviceDirectory, Option<CommitId>.None, cancellationToken);
            await validatePublisher(publisherOptions, Option<CommitId>.None, serviceName, serviceDirectory, cancellationToken);
        }

        async ValueTask publishCommitsAndValidate(PublisherOptions publisherOptions, IEnumerable<ServiceModel> serviceModels, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            var commits = await writeCommits(serviceModels, serviceDirectory, cancellationToken);
            await runPublisher(publisherOptions, serviceName, serviceDirectory, commits.HeadOrNone(), cancellationToken);
            await validatePublisher(publisherOptions, commits.HeadOrNone(), serviceName, serviceDirectory, cancellationToken);
        }

        async ValueTask cleanUp(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
        {
            if (serviceName.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                await deleteApimService(serviceName, cancellationToken);
            }

            Common.DeleteServiceDirectory(serviceDirectory);
        }

        Gen<(ServiceModel Model, ImmutableArray<ServiceModel> CommitModels, PublisherOptions Options, ManagementServiceName ServiceName, ManagementServiceDirectory ServiceDirectory)> generateFixture() =>
            from serviceModel in ServiceModel.Generate()
            from commitModels in generateCommitModels(serviceModel)
            from publisherOptions in PublisherOptions.Generate(serviceModel)
            from serviceName in Common.GenerateManagementServiceName(configuration, configurationKey: "FIRST_API_MANAGEMENT_SERVICE_NAME")
            from serviceDirectory in Common.GenerateManagementServiceDirectory()
            select (serviceModel, commitModels, publisherOptions, serviceName, serviceDirectory);

        static Gen<ImmutableArray<ServiceModel>> generateCommitModels(ServiceModel initialModel) =>
            from list in Gen.Int.List[1, 10]
            from aggregate in list.Aggregate(Gen.Const(ImmutableArray<ServiceModel>.Empty),
                                             (gen, _) => from commits in gen
                                                         let lastCommit = commits.LastOrDefault(initialModel)
                                                         from newCommit in generateUpdatedServiceModel(lastCommit)
                                                         select commits.Add(newCommit))
            select aggregate;

        static Gen<ServiceModel> generateUpdatedServiceModel(ServiceModel initialModel) =>
            from namedValues in Generator.GenerateNewSet(initialModel.NamedValues, NamedValueModel.GenerateSet(), NamedValueModule.GenerateUpdate)
            from tags in Generator.GenerateNewSet(initialModel.Tags, TagModel.GenerateSet(), TagModule.GenerateUpdate)
            from versionSets in Generator.GenerateNewSet(initialModel.VersionSets, VersionSetModel.GenerateSet(), VersionSetModule.GenerateUpdate)
            from backends in Generator.GenerateNewSet(initialModel.Backends, BackendModel.GenerateSet(), BackendModule.GenerateUpdate)
            from loggers in Generator.GenerateNewSet(initialModel.Loggers, LoggerModel.GenerateSet(), LoggerModule.GenerateUpdate)
            from diagnostics in from diagnosticSet in Generator.GenerateNewSet(initialModel.Diagnostics, DiagnosticModel.GenerateSet(), DiagnosticModule.GenerateUpdate)
                                from updatedDiagnostics in ServiceModel.UpdateDiagnostics(diagnosticSet, loggers)
                                select updatedDiagnostics
            from policyFragments in Generator.GenerateNewSet(initialModel.PolicyFragments, PolicyFragmentModel.GenerateSet(), PolicyFragmentModule.GenerateUpdate)
            from servicePolicies in Generator.GenerateNewSet(initialModel.ServicePolicies, ServicePolicyModel.GenerateSet(), ServicePolicyModule.GenerateUpdate)
            from groups in Generator.GenerateNewSet(initialModel.Groups, GroupModel.GenerateSet(), GroupModule.GenerateUpdate)
            from apis in from apiSet in Generator.GenerateNewSet(initialModel.Apis, ApiModel.GenerateSet(), ApiModule.GenerateUpdate)
                         from updatedApis in ServiceModel.UpdateApis(apiSet, versionSets, tags, loggers)
                         select updatedApis
            from products in from productSet in Generator.GenerateNewSet(initialModel.Products, ProductModel.GenerateSet(), ProductModule.GenerateUpdate)
                             from updatedProductGroups in ServiceModel.UpdateProducts(productSet, groups, tags, apis)
                             select updatedProductGroups
            from gateways in from gatewaySet in Generator.GenerateNewSet(initialModel.Gateways, GatewayModel.GenerateSet(), GatewayModule.GenerateUpdate)
                             from updatedGatewayApis in ServiceModel.UpdateGateways(gatewaySet, apis)
                             select updatedGatewayApis
            from subscriptions in from subscriptionSet in Generator.GenerateNewSet(initialModel.Subscriptions, SubscriptionModel.GenerateSet(), SubscriptionModule.GenerateUpdate)
                                  from updatedSubscriptions in ServiceModel.UpdateSubscriptions(subscriptionSet, products, apis)
                                  select updatedSubscriptions
            select initialModel with
            {
                NamedValues = namedValues,
                Tags = tags,
                Gateways = gateways,
                VersionSets = versionSets,
                Backends = backends,
                Loggers = loggers,
                Diagnostics = diagnostics,
                PolicyFragments = policyFragments,
                ServicePolicies = servicePolicies,
                Products = products,
                Groups = groups,
                Apis = apis,
                Subscriptions = subscriptions
            };
    }

    public static void ConfigureCleanUpTests(IHostApplicationBuilder builder)
    {
        ManagementServiceModule.ConfigureListApimServiceNames(builder);
        ManagementServiceModule.ConfigureDeleteApimService(builder);

        builder.Services.TryAddSingleton(GetCleanUpTests);
    }

    private static CleanUpTests GetCleanUpTests(IServiceProvider provider)
    {
        var listServiceNames = provider.GetRequiredService<ListApimServiceNames>();
        var deleteApimService = provider.GetRequiredService<DeleteApimService>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(CleanUpTests));

            logger.LogInformation("Cleaning up tests...");

            await listServiceNames(cancellationToken)
                    .Where(name => name.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
                    .IterParallel(deleteApimService.Invoke, cancellationToken);
        };
    }

    public static void ConfigureTestWorkspaces(IHostApplicationBuilder builder)
    {
        WorkspaceModule.ConfigureDeleteAllWorkspaces(builder);
        WorkspaceModule.ConfigurePutWorkspaceModels(builder);
        ExtractorModule.ConfigureRunExtractor(builder);
        WorkspaceModule.ConfigureValidateExtractedWorkspaces(builder);
        WorkspaceModule.ConfigureWriteWorkspaceModels(builder);
        PublisherModule.ConfigureRunPublisher(builder);
        WorkspaceModule.ConfigureValidatePublishedWorkspaces(builder);

        builder.Services.TryAddSingleton(GetTestWorkspaces);
    }

    private static TestWorkspaces GetTestWorkspaces(IServiceProvider provider)
    {
        var deleteWorkspaces = provider.GetRequiredService<DeleteAllWorkspaces>();
        var putWorkspaces = provider.GetRequiredService<PutWorkspaceModels>();
        var runExtractor = provider.GetRequiredService<RunExtractor>();
        var validateExtractor = provider.GetRequiredService<ValidateExtractedWorkspaces>();
        var writeWorkspaces = provider.GetRequiredService<WriteWorkspaceModels>();
        var runPublisher = provider.GetRequiredService<RunPublisher>();
        var validatePublisher = provider.GetRequiredService<ValidatePublishedWorkspaces>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var activitySource = provider.GetRequiredService<ActivitySource>();
        var logger = provider.GetRequiredService<ILogger>();

        var firstServiceName = ManagementServiceName.From(configuration.GetValue("FIRST_SERVICE_NAME"));
        var secondServiceName = ManagementServiceName.From(configuration.GetValue("SECOND_SERVICE_NAME"));

        return async cancellationToken =>
        {
            using var _ = activitySource.StartActivity(nameof(TestWorkspaces));

            await testExtractor(cancellationToken);
        };

        async ValueTask testExtractor(CancellationToken cancellationToken)
        {
            using var _ = activitySource.StartActivity("TestWorkspaceExtractor");

            logger.LogInformation("Testing workspace extractor...");

            var generator = from workspaces in WorkspaceModel.GenerateSet()
                            from optionalNamesToExport in ExtractorOptions.GenerateOptionalNamesToExport<WorkspaceName, WorkspaceModel>(workspaces)
                            from serviceDirectory in Common.GenerateManagementServiceDirectory()
                            select (workspaces, optionalNamesToExport, serviceDirectory);

            await generator.SampleAsync(async fixture =>
            {
                var (workspaces, optionalNamesToExport, serviceDirectory) = fixture;

                await deleteWorkspaces(firstServiceName, cancellationToken);
                await putWorkspaces(workspaces, firstServiceName, cancellationToken);

                var extractorOptions = ExtractorOptions.NoFilter with { WorkspaceNamesToExport = optionalNamesToExport };
                await runExtractor(extractorOptions, firstServiceName, serviceDirectory, cancellationToken);

                await validateExtractor(optionalNamesToExport, firstServiceName, serviceDirectory, cancellationToken);

                await deleteWorkspaces(firstServiceName, cancellationToken);
                serviceDirectory.ToDirectoryInfo().ForceDelete();
            }, iter: 1);
        }
    }
}

file static class Common
{
#pragma warning disable CA1802 // Use literals where appropriate
#pragma warning disable CA1805 // Do not initialize unnecessarily
    public static string TestServiceNamePrefix { get; } = "apiopsinttest-";
    public static readonly bool useExistingInstance = false;
#pragma warning restore CA1805 // Do not initialize unnecessarily
#pragma warning restore CA1802 // Use literals where appropriate

    public static void DeleteServiceDirectory(ManagementServiceDirectory serviceDirectory) => serviceDirectory.ToDirectoryInfo().ForceDelete();

    public static Gen<ManagementServiceName> GenerateManagementServiceName(IConfiguration configuration, string configurationKey) =>
        configuration.TryGetValue(configurationKey)
                     .Map(ManagementServiceName.From)
                     .Map(Gen.Const)
                     .IfNone(() => GenerateManagementServiceName(TestServiceNamePrefix));

    public static Gen<ManagementServiceName> GenerateManagementServiceName(string prefix) =>
        from lorem in Generator.Lorem
        let characters = lorem.Paragraphs(3)
                              // We want the name to change between tests, even if the seed is the same.
                              // This avoids soft-delete issues with APIM
                              .Concat(Guid.NewGuid().ToString())
                              .Where(char.IsLetterOrDigit)
                              .Select(char.ToLowerInvariant)
                              .ToArray()
        from suffixCharacters in Gen.Shuffle(characters, 8)
        let name = $"{prefix}{new string(suffixCharacters)}"
        select ManagementServiceName.From(name);

    public static Gen<ManagementServiceDirectory> GenerateManagementServiceDirectory() =>
        from lorem in Generator.Lorem
        let characters = lorem.Paragraphs(3)
                              .Where(char.IsLetterOrDigit)
                              .Select(char.ToLowerInvariant)
                              .ToArray()
        from suffixCharacters in Gen.Shuffle(characters, 8)
        let name = $"apiops-{new string(suffixCharacters)}"
        let path = Path.Combine(Path.GetTempPath(), name, "artifacts")
        let directoryInfo = new DirectoryInfo(path)
        select ManagementServiceDirectory.From(directoryInfo);
}