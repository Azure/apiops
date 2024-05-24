using common;
using common.tests;
using CsCheck;
using LanguageExt;
using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace integration.tests;

internal sealed record Fixture
{
    public required ManagementServiceName FirstServiceName { get; init; }
    public required ManagementServiceName SecondServiceName { get; init; }
    public required ServiceModel ServiceModel { get; init; }
    public required ServiceModel PublishAllChangesModel { get; init; }
    public required ImmutableArray<ServiceModel> CommitModels { get; init; }
    public required ManagementServiceDirectory ServiceDirectory { get; init; }
    public required ExtractorOptions ExtractorOptions { get; init; }
    public required PublisherOptions PublisherOptions { get; init; }

    public static Gen<Fixture> Generate(string managementServiceNamePrefix) =>
        from firstServiceName in GenerateManagementServiceName(managementServiceNamePrefix)
        from secondServiceName in GenerateManagementServiceName(managementServiceNamePrefix)
        from serviceModel in ServiceModel.Generate()
        from publishAllChangesModel in GeneratePublishAllChangesModel(serviceModel)
        from commitModels in GenerateCommitModels(serviceModel)
        from serviceDirectory in GetManagementServiceDirectory()
        from extractorOptions in ExtractorOptions.Generate(serviceModel)
        from publisherOptions in PublisherOptions.Generate(serviceModel)
        select new Fixture
        {
            FirstServiceName = firstServiceName,
            SecondServiceName = secondServiceName,
            ServiceModel = serviceModel,
            PublishAllChangesModel = publishAllChangesModel,
            CommitModels = commitModels,
            ServiceDirectory = serviceDirectory,
            ExtractorOptions = extractorOptions,
            PublisherOptions = publisherOptions
        };

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

    private static Gen<ManagementServiceDirectory> GetManagementServiceDirectory() =>
        from lorem in Generator.Lorem
        let characters = lorem.Paragraphs(3)
                              .Where(char.IsLetterOrDigit)
                              .Select(char.ToLowerInvariant)
                              .ToArray()
        from suffixCharacters in Gen.Shuffle(characters, 8)
        let name = $"apiops-{new string(suffixCharacters)}"
        let path = Path.Combine(Path.GetTempPath(), name)
        let directoryInfo = new DirectoryInfo(path)
        select ManagementServiceDirectory.From(directoryInfo);

    private static Gen<ServiceModel> GeneratePublishAllChangesModel(ServiceModel originalModel)
    {
        // Publishing all changes does not support deletes, so we only add items.
        var changeParameter = ChangeParameters.None with { Add = true };
        return GenerateUpdatedServiceModel(originalModel, changeParameter);
    }

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

    private static Gen<ImmutableArray<ServiceModel>> GenerateCommitModels(ServiceModel initialModel) =>
        from list in Gen.Int.List[1, 10]
        from aggregate in list.Aggregate(Gen.Const(ImmutableArray<ServiceModel>.Empty),
                                                    (gen, _) => from commits in gen
                                                                let lastCommit = commits.LastOrDefault(initialModel)
                                                                from newCommit in GenerateUpdatedServiceModel(lastCommit, ChangeParameters.All)
                                                                select commits.Add(newCommit))
        select aggregate;

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