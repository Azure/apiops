using CsCheck;
using LanguageExt;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace common.tests;

public record ServiceModel
{
    public required FrozenSet<NamedValueModel> NamedValues { get; init; }
    public required FrozenSet<TagModel> Tags { get; init; }
    public required FrozenSet<GatewayModel> Gateways { get; init; }
    public required FrozenSet<VersionSetModel> VersionSets { get; init; }
    public required FrozenSet<BackendModel> Backends { get; init; }
    public required FrozenSet<LoggerModel> Loggers { get; init; }
    public required FrozenSet<DiagnosticModel> Diagnostics { get; init; }
    public required FrozenSet<PolicyFragmentModel> PolicyFragments { get; init; }
    public required FrozenSet<ServicePolicyModel> ServicePolicies { get; init; }
    public required FrozenSet<GroupModel> Groups { get; init; }
    public required FrozenSet<ProductModel> Products { get; init; }
    public required FrozenSet<SubscriptionModel> Subscriptions { get; init; }
    public required FrozenSet<ApiModel> Apis { get; init; }

    public static Gen<ServiceModel> Generate() =>
        from namedValues in NamedValueModel.GenerateSet()
        from tags in TagModel.GenerateSet()
        from versionSets in VersionSetModel.GenerateSet()
        from backends in BackendModel.GenerateSet()
        from loggers in LoggerModel.GenerateSet()
        from diagnostics in from originalDiagnostics in DiagnosticModel.GenerateSet()
                            from updatedDiagnostics in UpdateDiagnostics(originalDiagnostics, loggers)
                            select updatedDiagnostics
        from policyFragments in PolicyFragmentModel.GenerateSet()
        from servicePolicies in ServicePolicyModel.GenerateSet()
        from apis in from originalApis in ApiModel.GenerateSet()
                     from updatedApis in UpdateApis(originalApis, versionSets, tags, loggers)
                     select updatedApis
        from groups in GroupModel.GenerateSet()
        from products in from originalProducts in ProductModel.GenerateSet()
                         from updatedProductGroups in UpdateProducts(originalProducts, groups, tags, apis)
                         select updatedProductGroups
        from gateways in from originalGateways in GatewayModel.GenerateSet()
                         from updatedGateways in UpdateGateways(originalGateways, apis)
                         select updatedGateways
        from subscriptions in from originalSubscriptions in SubscriptionModel.GenerateSet()
                              from updatedSubscriptions in UpdateSubscriptions(originalSubscriptions, products, apis)
                              select updatedSubscriptions
        select new ServiceModel
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
            Subscriptions = subscriptions,
            Apis = apis
        };

    public static Gen<FrozenSet<ApiModel>> UpdateApis(FrozenSet<ApiModel> apis,
                                                      ICollection<VersionSetModel> versionSets,
                                                      ICollection<TagModel> tags,
                                                      ICollection<LoggerModel> loggers) =>
        apis.Select(api => from version in UpdateApiVersion(versionSets)
                           from revisions in UpdateApiRevisions(api.Revisions, tags)
                           from diagnostics in ApiDiagnosticModel.GenerateSet(api.Diagnostics, loggers)
                           select api with
                           {
                               Version = version,
                               Revisions = revisions
                           })
            .SequenceToFrozenSet(apis.Comparer);


    private static Gen<Option<ApiVersion>> UpdateApiVersion(ICollection<VersionSetModel> versionSets)
    {
        if (versionSets.Count == 0)
        {
            return Gen.Const(Option<ApiVersion>.None);
        }

        var versionSetNames = versionSets.Select(x => x.Name).ToArray();

        return Gen.OneOfConst(versionSetNames)
                  .SelectMany(ApiVersion.Generate)
                  .OptionOf();
    }

    private static Gen<FrozenSet<ApiRevision>> UpdateApiRevisions(FrozenSet<ApiRevision> revisions, ICollection<TagModel> tags) =>
        revisions.Select(revision => from tags in UpdateApiTags(revision.Tags, tags)
                                     select revision with
                                     {
                                         Tags = tags
                                     })
                 .SequenceToFrozenSet(revisions.Comparer);

    private static Gen<FrozenSet<ApiTagModel>> UpdateApiTags(FrozenSet<ApiTagModel> apiTags, ICollection<TagModel> tags)
    {
        if (tags.Count == 0)
        {
            return Gen.Const(Array.Empty<ApiTagModel>()
                                  .ToFrozenSet(apiTags.Comparer));
        }

        var tagNames = tags.Select(tag => tag.Name)
                           .ToImmutableArray();

        return from apiTagNames in Generator.SubImmutableArrayOf(tagNames)
               select apiTagNames.Select(tagName => new ApiTagModel { Name = tagName })
                                 .ToFrozenSet(apiTags.Comparer);
    }

    public static Gen<FrozenSet<DiagnosticModel>> UpdateDiagnostics(FrozenSet<DiagnosticModel> diagnostics, ICollection<LoggerModel> loggers)
    {
        if (loggers.Count == 0)
        {
            // We could simply do Gen.Const(FrozenSet<DiagnosticModel>.Empty), but then we'd lose the original comparer.
            return Gen.Const(Array.Empty<DiagnosticModel>()
                                  .ToFrozenSet(diagnostics.Comparer));
        }

        var loggersArray = loggers.ToArray();

        return from updates in diagnostics.Select(diagnostic => from logger in Gen.OneOfConst(loggersArray)
                                                                select diagnostic with
                                                                {
                                                                    // Diagnostic name must be "azuremonitor" if the logger type is AzureMonitor
                                                                    Name = logger.Type is LoggerType.AzureMonitor ? DiagnosticName.From("azuremonitor") : diagnostic.Name,
                                                                    LoggerName = logger.Name
                                                                })
                                          .SequenceToFrozenSet(diagnostics.Comparer)
               select updates;
    }

    public static Gen<FrozenSet<SubscriptionModel>> UpdateSubscriptions(FrozenSet<SubscriptionModel> subscriptions, ICollection<ProductModel> products, ICollection<ApiModel> apis) =>
        from productSubscriptions in Generator.SubFrozenSetOf(subscriptions, subscriptions.Comparer)
        from updatedProductSubscriptions in UpdateSubscriptions(productSubscriptions, products)
        from apiSubscriptions in Generator.SubFrozenSetOf(subscriptions, subscriptions.Comparer)
        from updatedApiSubscriptions in UpdateSubscriptions(apiSubscriptions, apis)
        select updatedProductSubscriptions.Union(updatedApiSubscriptions).ToFrozenSet(subscriptions.Comparer);

    private static Gen<FrozenSet<SubscriptionModel>> UpdateSubscriptions(FrozenSet<SubscriptionModel> subscriptions, ICollection<ProductModel> products)
    {
        if (products.Count == 0)
        {
            return Gen.Const(Array.Empty<SubscriptionModel>()
                                  .ToFrozenSet(subscriptions.Comparer));
        }

        var productsArray = products.ToArray();

        return from updates in subscriptions.Select(subscription => from product in Gen.OneOfConst(productsArray)
                                                                    select subscription with
                                                                    {
                                                                        Scope = new SubscriptionScope.Product { Name = product.Name }
                                                                    })
                                          .SequenceToFrozenSet(subscriptions.Comparer)
               select updates;
    }

    private static Gen<FrozenSet<SubscriptionModel>> UpdateSubscriptions(FrozenSet<SubscriptionModel> subscriptions, ICollection<ApiModel> apis)
    {
        if (apis.Count == 0)
        {
            return Gen.Const(Array.Empty<SubscriptionModel>()
                                  .ToFrozenSet(subscriptions.Comparer));
        }

        var apisArray = apis.ToArray();

        return from updates in subscriptions.Select(subscription => from api in Gen.OneOfConst(apisArray)
                                                                    select subscription with
                                                                    {
                                                                        Scope = new SubscriptionScope.Api { Name = api.Name }
                                                                    })
                                          .SequenceToFrozenSet(subscriptions.Comparer)
               select updates;
    }

    public static Gen<FrozenSet<ProductModel>> UpdateProducts(FrozenSet<ProductModel> products,
                                                              ICollection<GroupModel> groups,
                                                              ICollection<TagModel> tags,
                                                              ICollection<ApiModel> apis) =>
        products.Select(product => from productGroups in UpdateProductGroups(product.Groups, groups)
                                   from productTags in UpdateProductTags(product.Tags, tags)
                                   from productApis in UpdateProductApis(product.Apis, apis)
                                   select product with
                                   {
                                       Groups = productGroups,
                                       Tags = productTags,
                                       Apis = productApis
                                   })
                .SequenceToFrozenSet(products.Comparer);

    private static Gen<FrozenSet<ProductGroupModel>> UpdateProductGroups(FrozenSet<ProductGroupModel> productGroups, ICollection<GroupModel> groups)
    {
        if (groups.Count == 0)
        {
            return Gen.Const(Array.Empty<ProductGroupModel>()
                                  .ToFrozenSet(productGroups.Comparer));
        }

        var groupNames = groups.Select(group => group.Name).ToArray();

        return productGroups.Select(productGroup => from groupName in Gen.OneOfConst(groupNames)
                                                    select productGroup with { Name = groupName })
                            .SequenceToFrozenSet(productGroups.Comparer);
    }

    private static Gen<FrozenSet<ProductTagModel>> UpdateProductTags(FrozenSet<ProductTagModel> productTags, ICollection<TagModel> tags)
    {
        if (tags.Count == 0)
        {
            return Gen.Const(Array.Empty<ProductTagModel>()
                                  .ToFrozenSet(productTags.Comparer));
        }

        var tagNames = tags.Select(tag => tag.Name).ToArray();

        return productTags.Select(productTag => from tagName in Gen.OneOfConst(tagNames)
                                                select productTag with { Name = tagName })
                            .SequenceToFrozenSet(productTags.Comparer);
    }

    private static Gen<FrozenSet<ProductApiModel>> UpdateProductApis(FrozenSet<ProductApiModel> productApis, ICollection<ApiModel> apis)
    {
        if (apis.Count == 0)
        {
            return Gen.Const(Array.Empty<ProductApiModel>()
                                  .ToFrozenSet(productApis.Comparer));
        }

        var apiNames = apis.Select(api => api.Name).ToArray();

        return productApis.Select(productApi => from apiName in Gen.OneOfConst(apiNames)
                                                select productApi with { Name = apiName })
                            .SequenceToFrozenSet(productApis.Comparer);
    }

    public static Gen<FrozenSet<GatewayModel>> UpdateGateways(FrozenSet<GatewayModel> gateways, ICollection<ApiModel> apis) =>
        gateways.Select(gateway => from gatewayApis in UpdateGatewayApis(gateway.Apis, apis)
                                   select gateway with
                                   {
                                       Apis = gatewayApis
                                   })
                .SequenceToFrozenSet(gateways.Comparer);

    private static Gen<FrozenSet<GatewayApiModel>> UpdateGatewayApis(FrozenSet<GatewayApiModel> gatewayApis, ICollection<ApiModel> apis)
    {
        if (apis.Count == 0)
        {
            return Gen.Const(Array.Empty<GatewayApiModel>()
                                  .ToFrozenSet(gatewayApis.Comparer));
        }

        var apiNames = apis.Select(api => api.Name).ToArray();

        return gatewayApis.Select(gatewayApi => from apiName in Gen.OneOfConst(apiNames)
                                                select gatewayApi with { Name = apiName })
                            .SequenceToFrozenSet(gatewayApis.Comparer);
    }
}