using common;
using common.tests;
using CsCheck;
using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using publisher;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace integration.tests;

internal delegate ValueTask RunTests(CancellationToken cancellationToken);

file delegate ValueTask TestExtractor(CancellationToken cancellationToken);

file delegate ValueTask TestExtractThenPublish(CancellationToken cancellationToken);

file delegate ValueTask TestPublisher(CancellationToken cancellationToken);

file delegate ValueTask CleanUpTests(CancellationToken cancellationToken);

file sealed class RunTestsHandler(ILogger<RunTests> logger,
                                  ActivitySource activitySource,
                                  TestExtractor testExtractor,
                                  TestExtractThenPublish testExtractThenPublish,
                                  TestPublisher testPublisher,
                                  CleanUpTests cleanUpTests)
{
    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(RunTests));

        logger.LogInformation("Running tests...");
        await testExtractor(cancellationToken);
        await testExtractThenPublish(cancellationToken);
        await testPublisher(cancellationToken);
        await cleanUpTests(cancellationToken);
    }
}

file sealed class TestExtractorHandler(ILogger<TestExtractor> logger,
                                       IConfiguration configuration,
                                       ActivitySource activitySource,
                                       CreateApimService createService,
                                       EmptyApimService emptyService,
                                       PutServiceModel putServiceModel,
                                       RunExtractor runExtractor,
                                       ValidateExtractorArtifacts validateExtractor,
                                       DeleteApimService deleteApimService)
{
    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(TestExtractor));

        logger.LogInformation("Testing extractor...");

        var generator = Fixture.Generate(configuration);
        await generator.SampleAsync(async fixture => await Run(fixture, cancellationToken), iter: 1);
    }

    private async ValueTask Run(Fixture fixture, CancellationToken cancellationToken)
    {
        await createService(fixture.ServiceName, cancellationToken);
        await emptyService(fixture.ServiceName, cancellationToken);
        await putServiceModel(fixture.ServiceModel, fixture.ServiceName, cancellationToken);
        await runExtractor(fixture.ExtractorOptions, fixture.ServiceName, fixture.ServiceDirectory, cancellationToken);
        await validateExtractor(fixture.ExtractorOptions, fixture.ServiceName, fixture.ServiceDirectory, cancellationToken);
        await CleanUp(fixture.ServiceName, fixture.ServiceDirectory, cancellationToken);
    }

    private async ValueTask CleanUp(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        if (serviceName.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await deleteApimService(serviceName, cancellationToken);
        }

        Common.DeleteServiceDirectory(serviceDirectory);
    }

    private sealed record Fixture
    {
        public required ExtractorOptions ExtractorOptions { get; init; }
        public required ServiceModel ServiceModel { get; init; }
        public required ManagementServiceName ServiceName { get; init; }
        public required ManagementServiceDirectory ServiceDirectory { get; init; }

        public static Gen<Fixture> Generate(IConfiguration configuration) =>
            from serviceModel in ServiceModel.Generate()
            from extractorOptions in ExtractorOptions.Generate(serviceModel)
            from serviceName in Common.useExistingInstance
                                ? Gen.Const(ManagementServiceName.From(configuration.GetValue("FIRST_API_MANAGEMENT_SERVICE_NAME")))
                                    : Common.GenerateManagementServiceName(Common.TestServiceNamePrefix)
            from serviceDirectory in Common.GenerateManagementServiceDirectory()
            select new Fixture
            {
                ExtractorOptions = extractorOptions,
                ServiceModel = serviceModel,
                ServiceName = serviceName,
                ServiceDirectory = serviceDirectory
            };
    }
}

file sealed class TestExtractThenPublishHandler(ILogger<TestExtractThenPublish> logger,
                                                IConfiguration configuration,
                                                ActivitySource activitySource,
                                                CreateApimService createService,
                                                PutServiceModel putServiceModel,
                                                RunExtractor runExtractor,
                                                DeleteApimService deleteApimService,
                                                RunPublisher runPublisher,
                                                ValidatePublishedArtifacts validatePublisher)
{
    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(TestExtractThenPublish));

        logger.LogInformation("Testing extracting, then publishing to a fresh instance...");

        var generator = Fixture.Generate(configuration);
        await generator.SampleAsync(async fixture => await Run(fixture, cancellationToken), iter: 1);
    }

    private async ValueTask Run(Fixture fixture, CancellationToken cancellationToken)
    {
        await CreateExtractorArtifacts(fixture.ServiceModel, fixture.SourceServiceName, fixture.ServiceDirectory, cancellationToken);
        await PublishToDestination(fixture.PublisherOptions, fixture.DestinationServiceName, fixture.ServiceDirectory, cancellationToken);
        await validatePublisher(fixture.PublisherOptions, Option<CommitId>.None, fixture.DestinationServiceName, fixture.ServiceDirectory, cancellationToken);
        await CleanUp(fixture.SourceServiceName, fixture.DestinationServiceName, fixture.ServiceDirectory, cancellationToken);
    }

    private async ValueTask CreateExtractorArtifacts(ServiceModel serviceModel, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(CreateExtractorArtifacts));

        logger.LogInformation("Creating extractor artifacts...");

        await createService(serviceName, cancellationToken);
        await putServiceModel(serviceModel, serviceName, cancellationToken);
        await runExtractor(ExtractorOptions.NoFilter, serviceName, serviceDirectory, cancellationToken);
        await deleteApimService(serviceName, cancellationToken);
    }

    private async ValueTask PublishToDestination(PublisherOptions publisherOptions, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(PublishToDestination));

        logger.LogInformation("Publishing artifacts to destination...");
        await createService(serviceName, cancellationToken);
        await runPublisher(publisherOptions, serviceName, serviceDirectory, Option<CommitId>.None, cancellationToken);
    }

    private async ValueTask CleanUp(ManagementServiceName sourceServiceName, ManagementServiceName destinationServiceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        await new[] { sourceServiceName, destinationServiceName }
                .Where(name => name.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
                .IterParallel(deleteApimService.Invoke, cancellationToken);

        Common.DeleteServiceDirectory(serviceDirectory);
    }

    private sealed record Fixture
    {
        public required PublisherOptions PublisherOptions { get; init; }
        public required ServiceModel ServiceModel { get; init; }
        public required ManagementServiceName SourceServiceName { get; init; }
        public required ManagementServiceName DestinationServiceName { get; init; }
        public required ManagementServiceDirectory ServiceDirectory { get; init; }

        public static Gen<Fixture> Generate(IConfiguration configuration) =>
            from serviceModel in ServiceModel.Generate()
            from publisherOptions in PublisherOptions.Generate(serviceModel)
            from sourceServiceName in GenerateManagementServiceName(configuration, "FIRST_API_MANAGEMENT_SERVICE_NAME")
            from destinationServiceName in GenerateManagementServiceName(configuration, "SECOND_API_MANAGEMENT_SERVICE_NAME")
            from serviceDirectory in Common.GenerateManagementServiceDirectory()
            select new Fixture
            {
                PublisherOptions = publisherOptions,
                ServiceModel = serviceModel,
                SourceServiceName = sourceServiceName,
                DestinationServiceName = destinationServiceName,
                ServiceDirectory = serviceDirectory
            };

        private static Gen<ManagementServiceName> GenerateManagementServiceName(IConfiguration configuration, string configurationKey) =>
            Common.useExistingInstance
            ? Gen.Const(ManagementServiceName.From(configuration.GetValue(configurationKey)))
            : Common.GenerateManagementServiceName(Common.TestServiceNamePrefix);
    }
}

file sealed class TestPublisherHandler(ILogger<TestPublisher> logger,
                                       IConfiguration configuration,
                                       ActivitySource activitySource,
                                       WriteServiceModelArtifacts writeArtifacts,
                                       CreateApimService createService,
                                       EmptyApimService emptyService,
                                       RunPublisher runPublisher,
                                       ValidatePublishedArtifacts validatePublisher,
                                       WriteServiceModelCommits writeCommits,
                                       DeleteApimService deleteApimService)
{
    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(TestPublisher));

        logger.LogInformation("Testing publisher...");

        var generator = Fixture.Generate(configuration);
        await generator.SampleAsync(async fixture => await Run(fixture, cancellationToken), iter: 1);
    }

    private async ValueTask Run(Fixture fixture, CancellationToken cancellationToken)
    {
        await PublishAllChangesAndValidate(fixture.PublisherOptions, fixture.ServiceModel, fixture.ServiceName, fixture.ServiceDirectory, cancellationToken);
        await PublishCommitsAndValidate(fixture.PublisherOptions, fixture.CommitModels, fixture.ServiceName, fixture.ServiceDirectory, cancellationToken);
        await CleanUp(fixture.ServiceName, fixture.ServiceDirectory, cancellationToken);
    }

    private async ValueTask PublishAllChangesAndValidate(PublisherOptions publisherOptions, ServiceModel serviceModel, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        await writeArtifacts(serviceModel, serviceDirectory, cancellationToken);
        await createService(serviceName, cancellationToken);
        await emptyService(serviceName, cancellationToken);
        await runPublisher(publisherOptions, serviceName, serviceDirectory, Option<CommitId>.None, cancellationToken);
        await validatePublisher(publisherOptions, Option<CommitId>.None, serviceName, serviceDirectory, cancellationToken);
    }

    private async ValueTask PublishCommitsAndValidate(PublisherOptions publisherOptions, IEnumerable<ServiceModel> serviceModels, ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        var commits = await writeCommits(serviceModels, serviceDirectory, cancellationToken);
        await runPublisher(publisherOptions, serviceName, serviceDirectory, commits.HeadOrNone(), cancellationToken);
        await validatePublisher(publisherOptions, commits.HeadOrNone(), serviceName, serviceDirectory, cancellationToken);
    }

    private async ValueTask CleanUp(ManagementServiceName serviceName, ManagementServiceDirectory serviceDirectory, CancellationToken cancellationToken)
    {
        if (serviceName.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await deleteApimService(serviceName, cancellationToken);
        }

        Common.DeleteServiceDirectory(serviceDirectory);
    }

    private sealed record Fixture
    {
        public required PublisherOptions PublisherOptions { get; init; }
        public required ServiceModel ServiceModel { get; init; }
        public required ImmutableArray<ServiceModel> CommitModels { get; init; }
        public required ManagementServiceName ServiceName { get; init; }
        public required ManagementServiceDirectory ServiceDirectory { get; init; }

        public static Gen<Fixture> Generate(IConfiguration configuration) =>
            from serviceModel in ServiceModel.Generate()
            from commitModels in GenerateCommitModels(serviceModel)
            from publisherOptions in PublisherOptions.Generate(serviceModel)
            from serviceName in GenerateManagementServiceName(configuration, "FIRST_API_MANAGEMENT_SERVICE_NAME")
            from serviceDirectory in Common.GenerateManagementServiceDirectory()
            select new Fixture
            {
                PublisherOptions = publisherOptions,
                ServiceModel = serviceModel,
                CommitModels = commitModels,
                ServiceName = serviceName,
                ServiceDirectory = serviceDirectory
            };

        private static Gen<ManagementServiceName> GenerateManagementServiceName(IConfiguration configuration, string configurationKey) =>
            Common.useExistingInstance
            ? Gen.Const(ManagementServiceName.From(configuration.GetValue(configurationKey)))
            : Common.GenerateManagementServiceName(Common.TestServiceNamePrefix);

        private static Gen<ImmutableArray<ServiceModel>> GenerateCommitModels(ServiceModel initialModel) =>
            from list in Gen.Int.List[1, 10]
            from aggregate in list.Aggregate(Gen.Const(ImmutableArray<ServiceModel>.Empty),
                                                        (gen, _) => from commits in gen
                                                                    let lastCommit = commits.LastOrDefault(initialModel)
                                                                    from newCommit in GenerateUpdatedServiceModel(lastCommit, ChangeParameters.All)
                                                                    select commits.Add(newCommit))
            select aggregate;

        private static Gen<ServiceModel> GenerateUpdatedServiceModel(ServiceModel initialModel, ChangeParameters changeParameters) =>
            from namedValues in GenerateNewSet(initialModel.NamedValues, NamedValueModel.GenerateSet(), NamedValue.GenerateUpdate, changeParameters)
            from tags in GenerateNewSet(initialModel.Tags, TagModel.GenerateSet(), Tag.GenerateUpdate, changeParameters)
            from versionSets in GenerateNewSet(initialModel.VersionSets, VersionSetModel.GenerateSet(), VersionSet.GenerateUpdate, changeParameters)
            from backends in GenerateNewSet(initialModel.Backends, BackendModel.GenerateSet(), Backend.GenerateUpdate, changeParameters)
            from loggers in GenerateNewSet(initialModel.Loggers, LoggerModel.GenerateSet(), Logger.GenerateUpdate, changeParameters)
            from diagnostics in from diagnosticSet in GenerateNewSet(initialModel.Diagnostics, DiagnosticModel.GenerateSet(), Diagnostic.GenerateUpdate, changeParameters)
                                from updatedDiagnostics in ServiceModel.UpdateDiagnostics(diagnosticSet, loggers)
                                select updatedDiagnostics
            from policyFragments in GenerateNewSet(initialModel.PolicyFragments, PolicyFragmentModel.GenerateSet(), PolicyFragment.GenerateUpdate, changeParameters)
            from servicePolicies in GenerateNewSet(initialModel.ServicePolicies, ServicePolicyModel.GenerateSet(), ServicePolicy.GenerateUpdate, changeParameters)
            from groups in GenerateNewSet(initialModel.Groups, GroupModel.GenerateSet(), Group.GenerateUpdate, changeParameters)
            from apis in from apiSet in GenerateNewSet(initialModel.Apis, ApiModel.GenerateSet(), Api.GenerateUpdate, changeParameters)
                         from updatedApis in ServiceModel.UpdateApis(apiSet, versionSets, tags)
                         select updatedApis
            from products in from productSet in GenerateNewSet(initialModel.Products, ProductModel.GenerateSet(), Product.GenerateUpdate, changeParameters)
                             from updatedProductGroups in ServiceModel.UpdateProducts(productSet, groups, tags, apis)
                             select updatedProductGroups
            from gateways in from gatewaySet in GenerateNewSet(initialModel.Gateways, GatewayModel.GenerateSet(), Gateway.GenerateUpdate, changeParameters)
                             from updatedGatewayApis in ServiceModel.UpdateGateways(gatewaySet, apis)
                             select updatedGatewayApis
            from subscriptions in from subscriptionSet in GenerateNewSet(initialModel.Subscriptions, SubscriptionModel.GenerateSet(), Subscription.GenerateUpdate, changeParameters)
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

        public static Gen<FrozenSet<T>> GenerateNewSet<T>(FrozenSet<T> original, Gen<FrozenSet<T>> newGen, Func<T, Gen<T>> updateGen) =>
            GenerateNewSet(original, newGen, updateGen, ChangeParameters.All);

        private static Gen<FrozenSet<T>> GenerateNewSet<T>(FrozenSet<T> original, Gen<FrozenSet<T>> newGen, Func<T, Gen<T>> updateGen, ChangeParameters changeParameters)
        {
            var generator = from originalItems in Gen.Const(original)
                            from itemsRemoved in changeParameters.Remove ? RemoveItems(originalItems) : Gen.Const(originalItems)
                            from itemsAdded in changeParameters.Add ? AddItems(itemsRemoved, newGen) : Gen.Const(itemsRemoved)
                            from itemsModified in changeParameters.Modify ? ModifyItems(itemsAdded, updateGen) : Gen.Const(itemsAdded)
                            select itemsModified;

            return changeParameters.MaxSize.Map(maxSize => generator.SelectMany(set => set.Count <= maxSize
                                                                                        ? generator
                                                                                        : from smallerSet in Gen.Shuffle(set.ToArray(), maxSize)

                                                                                          select smallerSet.ToFrozenSet(set.Comparer)))
                .IfNone(generator);
        }

        private static Gen<FrozenSet<T>> RemoveItems<T>(FrozenSet<T> set) =>
            from itemsToRemove in Generator.SubFrozenSetOf(set)
            select set.Except(itemsToRemove, set.Comparer).ToFrozenSet(set.Comparer);

        private static Gen<FrozenSet<T>> AddItems<T>(FrozenSet<T> set, Gen<FrozenSet<T>> gen) =>
            from itemsToAdd in gen
            select set.Append(itemsToAdd).ToFrozenSet(set.Comparer);

        private static Gen<FrozenSet<T>> ModifyItems<T>(FrozenSet<T> set, Func<T, Gen<T>> updateGen) =>
            from itemsToModify in Generator.SubFrozenSetOf(set)
            from modifiedItems in itemsToModify.Select(updateGen).SequenceToImmutableArray()
            select set.Except(itemsToModify).Append(modifiedItems).ToFrozenSet(set.Comparer);

        private sealed record ChangeParameters
        {
            public required bool Add { get; init; }
            public required bool Modify { get; init; }
            public required bool Remove { get; init; }

            public static ChangeParameters None { get; } = new()
            {
                Add = false,
                Modify = false,
                Remove = false
            };

            public static ChangeParameters All { get; } = new()
            {
                Add = true,
                Modify = true,
                Remove = true
            };

            public Option<int> MaxSize { get; init; } = Option<int>.None;
        }
    }
}

file sealed class CleanUpTestsHandler(ILogger<CleanUpTests> logger,
                                     ActivitySource activitySource,
                                     ListApimServiceNames listApimServiceNames,
                                     DeleteApimService deleteApimService)
{
    public async ValueTask Handle(CancellationToken cancellationToken)
    {
        using var _ = activitySource.StartActivity(nameof(CleanUpTests));

        logger.LogInformation("Cleaning up test...");

        await listApimServiceNames(cancellationToken)
                .Where(name => name.ToString().StartsWith(Common.TestServiceNamePrefix, StringComparison.OrdinalIgnoreCase))
                .IterParallel(deleteApimService.Invoke, cancellationToken);
    }
}

internal static class TestServices
{
    public static void ConfigureRunTests(IServiceCollection services)
    {
        ConfigureTestExtractor(services);
        ConfigureTestExtractThenPublish(services);
        ConfigureTestPublisher(services);
        ConfigureCleanUpTests(services);

        services.TryAddSingleton<RunTestsHandler>();
        services.TryAddSingleton<RunTests>(provider => provider.GetRequiredService<RunTestsHandler>().Handle);
    }

    private static void ConfigureTestExtractor(IServiceCollection services)
    {
        ManagementServices.ConfigureCreateApimService(services);
        ManagementServices.ConfigureEmptyApimService(services);
        ManagementServices.ConfigurePutServiceModel(services);
        ExtractorServices.ConfigureRunExtractor(services);
        ExtractorServices.ConfigureValidateExtractorArtifacts(services);
        ManagementServices.ConfigureDeleteApimService(services);

        services.TryAddSingleton<TestExtractorHandler>();
        services.TryAddSingleton<TestExtractor>(provider => provider.GetRequiredService<TestExtractorHandler>().Handle);
    }

    private static void ConfigureTestExtractThenPublish(IServiceCollection services)
    {
        ManagementServices.ConfigureCreateApimService(services);
        ManagementServices.ConfigurePutServiceModel(services);
        ExtractorServices.ConfigureRunExtractor(services);
        ManagementServices.ConfigureDeleteApimService(services);
        PublisherServices.ConfigureRunPublisher(services);
        PublisherServices.ConfigureValidatePublishedArtifacts(services);

        services.TryAddSingleton<TestExtractThenPublishHandler>();
        services.TryAddSingleton<TestExtractThenPublish>(provider => provider.GetRequiredService<TestExtractThenPublishHandler>().Handle);
    }

    private static void ConfigureTestPublisher(IServiceCollection services)
    {
        ManagementServices.ConfigureWriteServiceModelArtifacts(services);
        ManagementServices.ConfigureCreateApimService(services);
        ManagementServices.ConfigureEmptyApimService(services);
        PublisherServices.ConfigureRunPublisher(services);
        PublisherServices.ConfigureValidatePublishedArtifacts(services);
        ManagementServices.ConfigureWriteServiceModelCommits(services);
        ManagementServices.ConfigureDeleteApimService(services);

        services.TryAddSingleton<TestPublisherHandler>();
        services.TryAddSingleton<TestPublisher>(provider => provider.GetRequiredService<TestPublisherHandler>().Handle);
    }

    private static void ConfigureCleanUpTests(IServiceCollection services)
    {
        ManagementServices.ConfigureListApimServiceNames(services);
        ManagementServices.ConfigureDeleteApimService(services);

        services.TryAddSingleton<CleanUpTestsHandler>();
        services.TryAddSingleton<CleanUpTests>(provider => provider.GetRequiredService<CleanUpTestsHandler>().Handle);
    }
}

file sealed class Common
{
#pragma warning disable CA1802 // Use literals where appropriate
#pragma warning disable CA1805 // Do not initialize unnecessarily
    public static string TestServiceNamePrefix { get; } = "apiopsinttest-";
    public static readonly bool useExistingInstance = false;
#pragma warning restore CA1805 // Do not initialize unnecessarily
#pragma warning restore CA1802 // Use literals where appropriate

    public static void DeleteServiceDirectory(ManagementServiceDirectory serviceDirectory) => serviceDirectory.ToDirectoryInfo().ForceDelete();

    // We want the name to change between tests, even if the seed is the same.
    // This avoids soft-delete issues with APIM
    public static Gen<ManagementServiceName> GenerateManagementServiceName(string prefix) =>
        from lorem in Generator.Lorem
        let characters = lorem.Paragraphs(3)
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